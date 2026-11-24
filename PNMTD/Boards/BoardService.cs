using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Connectors;
using PNMTD.Data;
using PNMTD.Files;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Redmine;
using PNMTD.Services.Domain;
using PNMTD.Zabbix;

namespace PNMTD.Boards;

/// <summary>
/// Boards in the admin UI (docs/boards-plan.md §10, §12.1): the list, a board in full, its creation and its save (the whole page at once,
/// from the revision it was loaded at), duplicating and deleting it, its URL shown and renewed, the address PNMT sees, and the catalog
/// for the editor. Adding an element or a rule needs its area's right (§7.2). Every change is in the audit log.
/// </summary>
public sealed class BoardService(PnmtdDbContext db, BoardTokenProtector protector, BoardRuntime runtime, PublicUrls urls, LocationTree tree,
    RedmineWorker redmine, IOptionsMonitor<RedmineOptions> redmineOptions, IOptionsMonitor<ZabbixOptions> zabbixOptions,
    IOptionsMonitor<BoardOptions> options, IConfiguration configuration, IHttpContextAccessor accessor)
{
    public const int MaxName = 100;

    private UserRights Rights => accessor.HttpContext?.User.Rights() ?? UserRights.None;

    // --- reading -----------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<BoardItem>> ListAsync(CancellationToken ct)
    {
        var rows = await db.Boards.AsNoTracking().OrderBy(b => b.Name).ToListAsync(ct);
        return rows.Select(b =>
        {
            var layout = BoardLayouts.Read(b.Layout);
            var networks = ConnectorNetworks.Read(b.Networks);
            return new BoardItem(b.Id, b.Name, b.Description, b.Enabled, layout.Columns.Count, BoardLayouts.Elements(layout).Count(), layout.Look.Theme,
                networks.Count, networks.Count == 0 || ConnectorNetworks.AnyAddress(networks), Utc(b.LastSeenAt),
                runtime.ScreensNow(b.Id, layout.RefreshSeconds), layout.Look.Messages, layout.Look.MessageSize);
        }).ToList();
    }

    public async Task<BoardDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var b = await db.Boards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Board");
        return Detail(b);
    }

    private BoardDetail Detail(BoardEntity b)
    {
        var layout = BoardLayouts.Read(b.Layout);
        var networks = ConnectorNetworks.Read(b.Networks);
        var inside = ConnectorNetworks.Inside(networks);
        var manage = Rights.Has(Area.Boards, Level.Manage);
        return new BoardDetail(b.Id, b.Name, b.Description, b.Enabled, manage ? b.TokenHint : null,
            networks.Select((n, i) => new BoardNetworkItem(n.Cidr, n.Label, inside[i])).ToList(), networks.Count == 0 || ConnectorNetworks.AnyAddress(networks),
            layout, b.Revision, new BoardSeen(Utc(b.LastSeenAt), b.LastSeenAddress, b.LastSeenAgent, runtime.ScreensNow(b.Id, layout.RefreshSeconds)),
            Clock.AsUtc(b.CreatedAt), b.CreatedByName, Clock.AsUtc(b.UpdatedAt), b.UpdatedByName);
    }

    private static DateTime? Utc(DateTime? value) => value is { } v ? Clock.AsUtc(v) : null;

    // --- changing ----------------------------------------------------------------------------------------------------------------

    public async Task<BoardDetail> CreateAsync(BoardCreateRequest request, CancellationToken ct)
    {
        var v = new ApiValidator();
        var name = await NameAsync(request.Name, null, v, ct);
        var description = Description(request.Description, v);
        BoardLayout layout;
        switch (request.From ?? "start")
        {
            case "copy":
                var source = request.CopyOf is { } copyOf ? await db.Boards.AsNoTracking().SingleOrDefaultAsync(b => b.Id == copyOf, ct) : null;
                v.Check(source is not null, "copyOf", "No such board.");
                layout = source is null ? BoardLayouts.New(1, false, false) : Fresh(BoardLayouts.Read(source.Layout));
                break;
            case "empty" or "start":
                v.Check(request.Columns is >= 1 and <= BoardLayouts.MaxColumns, "columns", $"1 to {BoardLayouts.MaxColumns} columns.");
                layout = BoardLayouts.New(request.Columns, request.From != "empty", zabbixOptions.CurrentValue.Enabled);
                break;
            default:
                v.Add("from", "empty, start or copy.");
                layout = BoardLayouts.New(1, false, false);
                break;
        }
        v.ThrowIfInvalid();
        RequireRights(null, layout);
        var board = Add(name, description, true, "[]", layout);
        await FileUses.RecordAsync(db, BoardLayouts.ImageIds(layout), FileOwner.Board(board.Id), ct);
        AuditLog.Target(Target(board));
        AuditLog.Summary($"{name} created: {BoardLayouts.Describe(layout)}; opens from anywhere until networks are set");
        await db.SaveChangesAsync(ct);
        return Detail(board);
    }

    /// <summary>A copy of a layout with new element ids: two boards never share one.</summary>
    private static BoardLayout Fresh(BoardLayout layout) => layout with
    {
        Columns = [.. layout.Columns.Select(c => c with { Elements = [.. c.Elements.Select(e => e with { Id = BoardLayouts.NewId() })] })],
    };

    private BoardEntity Add(string name, string? description, bool enabled, string networks, BoardLayout layout)
    {
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        var token = BoardTokens.New();
        var board = new BoardEntity
        {
            Id = Guid.NewGuid(), Name = name, NormalizedName = Normalize(name), Description = description, Enabled = enabled, Networks = networks,
            Layout = BoardLayouts.Write(layout), TokenHash = BoardTokens.Hash(token), TokenProtected = protector.Protect(token), TokenHint = BoardTokens.Hint(token),
            CreatedAt = now, CreatedBy = by.Id, CreatedByName = by.Name, UpdatedAt = now, UpdatedBy = by.Id, UpdatedByName = by.Name,
        };
        db.Boards.Add(board);
        return board;
    }

    /// <summary>The whole page (§10.3): name, description, enabled, networks and layout, from the revision it was loaded at.</summary>
    public async Task<BoardDetail> UpdateAsync(Guid id, BoardRequest request, CancellationToken ct)
    {
        var b = await db.Boards.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Board");
        if (request.Revision != b.Revision)
        {
            throw ApiException.EditConflict(b.Revision, b.UpdatedByName, Clock.AsUtc(b.UpdatedAt),
                $"{b.UpdatedByName ?? "Someone"} saved this board in between. Load it again to see their changes.");
        }
        var v = new ApiValidator();
        var name = await NameAsync(request.Name, id, v, ct);
        var description = Description(request.Description, v);
        var networks = Networks(request.Networks, v);
        var layout = BoardLayouts.Validate(request.Layout, v);
        await CheckImagesAsync(id, layout, v, ct);
        v.ThrowIfInvalid();
        var before = BoardLayouts.Read(b.Layout);
        RequireRights(before, layout);
        await FileUses.SyncBoardAsync(db, id, BoardLayouts.ImageIds(layout), ct);

        var parts = new List<string>();
        if (b.Name != name)
        {
            parts.Add($"renamed {name}");
        }
        if (b.Description != description)
        {
            parts.Add("description changed");
        }
        if (b.Enabled != request.Enabled)
        {
            parts.Add(request.Enabled ? "switched on" : "switched off");
        }
        var oldNetworks = ConnectorNetworks.Read(b.Networks);
        var newNetworks = networks.Count == 0 ? "anywhere" : string.Join(", ", networks.Select(n => n.Cidr));
        if (string.Join(",", oldNetworks.Select(n => n.Cidr)) != string.Join(",", networks.Select(n => n.Cidr)))
        {
            parts.Add($"networks: {newNetworks}");
        }
        if (BoardLayouts.Diff(before, layout) is { } diff)
        {
            parts.Add(diff);
        }
        var oldName = b.Name;
        b.Name = name;
        b.NormalizedName = Normalize(name);
        b.Description = description;
        b.Enabled = request.Enabled;
        b.Networks = ConnectorNetworks.Write(networks);
        b.Layout = BoardLayouts.Write(layout);
        b.Revision++;
        var by = LocationActor.Of(accessor);
        b.UpdatedAt = Clock.UtcNow;
        b.UpdatedBy = by.Id;
        b.UpdatedByName = by.Name;
        AuditLog.Target(Target(b));
        AuditLog.Summary(parts.Count == 0 ? $"{oldName}: saved, nothing changed" : $"{oldName}: {string.Join("; ", parts)}");
        await db.SaveChangesAsync(ct);
        runtime.Forget(id);
        return Detail(b);
    }

    public async Task<BoardDetail> DuplicateAsync(Guid id, CancellationToken ct)
    {
        var source = await db.Boards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Board");
        var layout = Fresh(BoardLayouts.Read(source.Layout));
        RequireRights(null, layout);
        var name = source.Name.Length > MaxName - 7 ? source.Name[..(MaxName - 7)] : source.Name;
        var candidate = $"{name} (copy)";
        for (var n = 2; await db.Boards.AnyAsync(b => b.NormalizedName == Normalize(candidate), ct); n++)
        {
            candidate = $"{name} (copy {n})";
        }
        var board = Add(candidate, source.Description, source.Enabled, "[]", layout);
        await FileUses.RecordAsync(db, BoardLayouts.ImageIds(layout), FileOwner.Board(board.Id), ct);
        AuditLog.Target(Target(board));
        AuditLog.Summary($"{candidate} created as a copy of {source.Name}: {BoardLayouts.Describe(layout)}; a URL of its own, no networks");
        await db.SaveChangesAsync(ct);
        return Detail(board);
    }

    /// <summary>At once: its URL stops working; the audit log keeps what it was.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var b = await db.Boards.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Board");
        AuditLog.Target(Target(b));
        AuditLog.Summary($"{b.Name} deleted: {BoardLayouts.Describe(BoardLayouts.Read(b.Layout))}; its URL …{b.TokenHint} stopped");
        db.Boards.Remove(b);
        await db.SaveChangesAsync(ct);
        runtime.Forget(id);
    }

    // --- images (docs/board-messages-plan.md §9.4) ---------------------------------------------------------------------------------

    /// <summary>
    /// A layout's images must be the board's own: uploaded for it, or on it already. An id from elsewhere (a page's image) would show
    /// a file the editor may not otherwise see.
    /// </summary>
    private async Task CheckImagesAsync(Guid id, BoardLayout layout, ApiValidator v, CancellationToken ct)
    {
        var wanted = BoardLayouts.ImageIds(layout);
        if (wanted.Count == 0)
        {
            return;
        }
        var own = await db.FileUses.AsNoTracking().Where(u => u.BoardId == id && wanted.Contains(u.FileId)).Select(u => u.FileId).ToListAsync(ct);
        foreach (var (column, c) in layout.Columns.Select((col, i) => (col, i)))
        {
            foreach (var (element, e) in column.Elements.Select((el, i) => (el, i)))
            {
                if (element.Type == BoardElementType.Image && element.Settings.FileId is { } file && !own.Contains(file))
                {
                    v.Add($"layout.columns[{c}].elements[{e}].settings.fileId", "Not one of this board's images: upload it for this board.");
                }
            }
        }
    }

    /// <summary>
    /// An image for a board (§9.4): checked by its bytes and stored as a knowledge base image is, up to <c>Boards:MaxImageBytes</c>; its
    /// use is recorded at once, so that the editor's preview shows it before the board is saved.
    /// </summary>
    public async Task<BoardImageItem> UploadImageAsync(Guid id, Stream body, string? name, FileActor actor, FileStore store, CancellationToken ct)
    {
        if (!await db.Boards.AnyAsync(b => b.Id == id, ct))
        {
            throw ApiException.NotFound("Board");
        }
        var item = await store.StoreAsync(body, FileKind.Image, name, null, actor, ct, options.CurrentValue.MaxImageBytes);
        await FileUses.RecordAsync(db, [item.Id], FileOwner.Board(id), ct);
        await db.SaveChangesAsync(ct);
        AuditLog.Target(AuditTarget.Of(AuditTargets.File, item.Id, item.Name));
        AuditLog.Summary($"{item.Name} ({item.ContentType}, {item.Size} bytes) for the board {await db.Boards.Where(b => b.Id == id).Select(b => b.Name).SingleAsync(ct)}");
        var created = await db.Files.AsNoTracking().Where(f => f.Id == item.Id).Select(f => f.CreatedAt).SingleAsync(ct);
        return new BoardImageItem(item.Id, item.Name, item.ContentType, item.Size, item.Width, item.Height, Clock.AsUtc(created));
    }

    /// <summary>The images a board uses, newest first: the editor's picker.</summary>
    public async Task<IReadOnlyList<BoardImageItem>> ImagesAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Boards.AnyAsync(b => b.Id == id, ct))
        {
            throw ApiException.NotFound("Board");
        }
        var files = await (from u in db.FileUses.AsNoTracking()
                           join f in db.Files.AsNoTracking() on u.FileId equals f.Id
                           where u.BoardId == id && f.Kind == FileKind.Image && f.DeletedAt == null
                           orderby f.CreatedAt descending
                           select f).ToListAsync(ct);
        return files.Select(f => new BoardImageItem(f.Id, f.Name, f.ContentType, f.Size, f.Width, f.Height, Clock.AsUtc(f.CreatedAt))).ToList();
    }

    /// <summary>One of a board's images, for the editor's preview: null unless the board uses it.</summary>
    public async Task<FileEntity?> ImageAsync(Guid id, Guid fileId, CancellationToken ct) =>
        await (from u in db.FileUses.AsNoTracking()
               join f in db.Files.AsNoTracking() on u.FileId equals f.Id
               where u.BoardId == id && u.FileId == fileId && f.Kind == FileKind.Image
               select f).FirstOrDefaultAsync(ct);

    /// <summary>The URL, shown (§5.1): audited. 409 when the key ring that protected it is gone: then only a new URL helps.</summary>
    public async Task<BoardUrl> UrlAsync(Guid id, CancellationToken ct)
    {
        var b = await db.Boards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Board");
        var token = protector.Unprotect(b.TokenProtected)
            ?? throw ApiException.Conflict("token_unreadable", "This URL can't be shown again (the key ring changed). Renew it to get a new one.");
        AuditLog.Target(Target(b));
        AuditLog.Summary($"{b.Name}: its URL …{b.TokenHint} shown");
        return new BoardUrl(Url(token));
    }

    /// <summary>A new token (§5.1): the old URL stops at once.</summary>
    public async Task<BoardUrl> RenewAsync(Guid id, CancellationToken ct)
    {
        var b = await db.Boards.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Board");
        var old = b.TokenHint;
        var token = BoardTokens.New();
        b.TokenHash = BoardTokens.Hash(token);
        b.TokenProtected = protector.Protect(token);
        b.TokenHint = BoardTokens.Hint(token);
        AuditLog.Target(Target(b));
        AuditLog.Summary($"{b.Name}: a new URL …{b.TokenHint}; the old one …{old} stopped");
        await db.SaveChangesAsync(ct);
        runtime.Forget(id);
        return new BoardUrl(Url(token));
    }

    /// <summary>The URL's address (§5.1): Boards__BaseUrl, else PublicBaseUrl, else the admin UI's own.</summary>
    public string Url(string token) => $"{(Text.Trimmed(options.CurrentValue.BaseUrl)?.TrimEnd('/') ?? urls.Base)}/pnmt-board/{token}";

    /// <summary>The address PNMT sees for the admin's browser (§5.2), with a hint when it is a Docker bridge's and Proxy isn't set.</summary>
    public BoardAddress Address()
    {
        var proxySet = !string.IsNullOrWhiteSpace(configuration["Proxy"]);
        var address = accessor.HttpContext?.Connection.RemoteIpAddress;
        if (address is null)
        {
            return new BoardAddress(null, null, proxySet);
        }
        var plain = ConnectorNetworks.Plain(address);
        var hint = Hosting.ProxySetting.Docker.Contains(plain) && !proxySet
            ? "This is a Docker bridge's address: PNMT sees the proxy, not your browser. Set Proxy=docker in .env (deploy/README.md), or the networks judge the proxy."
            : null;
        return new BoardAddress(plain.ToString(), hint, proxySet);
    }

    // --- the catalog (§10.3) -----------------------------------------------------------------------------------------------------

    public async Task<BoardCatalog> CatalogAsync(CancellationToken ct)
    {
        var rights = Rights;
        bool Configured(string? needs) => needs switch
        {
            "redmine" => redmineOptions.CurrentValue.Enabled,
            "zabbix" => zabbixOptions.CurrentValue.Enabled,
            _ => true,
        };
        string? Why((Area Area, Level Level)? right, string? needs) =>
            !Configured(needs) ? $"{(needs == "redmine" ? "Redmine" : "Zabbix")} isn't set up in PNMT."
            : right is { } r && !rights.Has(r.Area, r.Level) ? $"Needs {RightsCatalog.Of(r.Area).Label}: {r.Level}."
            : null;
        var types = BoardLayouts.Types.Select(t =>
        {
            var why = Why(t.Right, t.Needs);
            return new BoardCatalogType(t.Type, t.Group, t.Name, t.Line, t.Icon, why is null, why);
        }).ToList();
        var metrics = BoardLayouts.Metrics.Select(m =>
        {
            var why = Why(m.Right, m.Needs);
            return new BoardCatalogMetric(m.Metric, m.Name, m.Filters, why is null, why);
        }).ToList();
        var snapshot = await tree.GetAsync(ct);
        var locations = snapshot.All.Select(n => new BoardLocationOption(n.Id, snapshot.Path(n.Id))).OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var projects = new List<BoardProjectOption>();
        if (redmineOptions.CurrentValue.Enabled && redmine.CachedLookups() is { } lookups)
        {
            projects = lookups.Projects.Where(p => lookups.Allowed.Contains(p.Id)).Select(p => new BoardProjectOption(p.Id, p.Name,
                p.Parent?.Id is { } parent && lookups.Allowed.Contains(parent) ? parent : null)).ToList();
        }
        var groups = zabbixOptions.CurrentValue.Enabled
            ? (await db.ZabbixHosts.AsNoTracking().Where(h => h.GoneAt == null).Select(h => h.Groups).ToListAsync(ct))
                .SelectMany(g => g).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        return new BoardCatalog(types, metrics, BoardThemes.Ids, locations, projects, groups, Clock.TimeZone.Id, options.CurrentValue.MaxImageBytes,
            Math.Clamp(options.CurrentValue.MessageMaxMinutes, 1, 1440) * 60);
    }

    /// <summary>Hosts and sensors for status tiles, by name (§12.1): at most 20, names only.</summary>
    public async Task<IReadOnlyList<BoardTargetOption>> TargetsAsync(string? q, CancellationToken ct)
    {
        var pattern = Text.Pattern(q);
        if (pattern is null)
        {
            return [];
        }
        var hosts = await db.Hosts.AsNoTracking().Where(h => EF.Functions.ILike(h.Name, pattern, @"\")).OrderBy(h => h.Name).Take(10)
            .Select(h => new BoardTargetOption(BoardTargetKind.Host, h.Id, h.Name)).ToListAsync(ct);
        var sensors = await db.Sensors.AsNoTracking()
            .Where(s => EF.Functions.ILike(s.Name, pattern, @"\") || EF.Functions.ILike(s.Parent.Name, pattern, @"\"))
            .OrderBy(s => s.Parent.Name).ThenBy(s => s.Name).Take(20 - hosts.Count)
            .Select(s => new BoardTargetOption(BoardTargetKind.Sensor, s.Id, s.Parent.Name + " / " + s.Name)).ToListAsync(ct);
        return [.. hosts, .. sensors];
    }

    /// <summary>The names of the hosts and sensors a layout's tiles name, for the editor.</summary>
    public async Task<IReadOnlyList<BoardTargetOption>> TargetNamesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        var hosts = await db.Hosts.AsNoTracking().Where(h => ids.Contains(h.Id)).Select(h => new BoardTargetOption(BoardTargetKind.Host, h.Id, h.Name))
            .ToListAsync(ct);
        var sensors = await db.Sensors.AsNoTracking().Where(s => ids.Contains(s.Id))
            .Select(s => new BoardTargetOption(BoardTargetKind.Sensor, s.Id, s.Parent.Name + " / " + s.Name)).ToListAsync(ct);
        return [.. hosts, .. sensors];
    }

    // --- checks ------------------------------------------------------------------------------------------------------------------

    /// <summary>§7.2: what the save adds or changes needs its area's right; what stays as it was needs nothing.</summary>
    private void RequireRights(BoardLayout? before, BoardLayout after)
    {
        var missing = BoardLayouts.Missing(before, after, Rights);
        if (missing.Count > 0)
        {
            var (what, area, level) = missing[0];
            throw new ApiException(StatusCodes.Status403Forbidden, "element_right",
                $"Adding or changing {what} needs {RightsCatalog.Of(area).Label}: {level}.",
                extensions: new Dictionary<string, object?> { ["area"] = area.ToString(), ["level"] = level.ToString(), ["element"] = what });
        }
    }

    private async Task<string> NameAsync(string? value, Guid? id, ApiValidator v, CancellationToken ct)
    {
        var name = value?.Trim() ?? "";
        v.Check(name.Length is >= 1 and <= MaxName && !name.Contains('\n'), "name", $"A name of 1 to {MaxName} characters, one line.");
        if (name.Length is >= 1 and <= MaxName)
        {
            var normalized = Normalize(name);
            if (await db.Boards.AnyAsync(b => b.NormalizedName == normalized && b.Id != id, ct))
            {
                throw ApiException.Conflict("duplicate_name", $"Another board is called {name}.");
            }
        }
        return name;
    }

    private static string? Description(string? value, ApiValidator v)
    {
        var description = Text.Trimmed(value);
        v.Check(description is null || description.Length <= 300, "description", "At most 300 characters.");
        return description;
    }

    private static List<ConnectorNetwork> Networks(IReadOnlyList<BoardNetworkRequest>? entries, ApiValidator v)
    {
        var list = entries ?? [];
        v.Check(list.Count <= ConnectorNetworks.MaxEntries, "networks", $"At most {ConnectorNetworks.MaxEntries} entries.");
        var networks = new List<ConnectorNetwork>();
        foreach (var (entry, i) in list.Select((n, i) => (n, i)))
        {
            var (cidr, error) = ConnectorNetworks.Normalize(entry.Cidr);
            if (error is not null)
            {
                v.Add($"networks[{i}].cidr", error);
                continue;
            }
            v.Check(entry.Label is null || entry.Label.Length <= ConnectorNetworks.MaxLabel, $"networks[{i}].label",
                $"At most {ConnectorNetworks.MaxLabel} characters.");
            networks.Add(new ConnectorNetwork(cidr!, Text.Trimmed(entry.Label)));
        }
        return networks;
    }

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();

    public static AuditTarget Target(BoardEntity b) => AuditTarget.Of(AuditTargets.Board, b.Id, b.Name);
}
