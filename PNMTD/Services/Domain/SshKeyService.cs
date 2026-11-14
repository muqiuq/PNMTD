using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Agents;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.SshKeyManager;

namespace PNMTD.Services.Domain;

/// <summary>
/// The SSH key manager in the API (docs/linux-sensor-plan.md §6.4, §6.5, §10): the register (list, a key's drawer, the parse preview,
/// registering by paste, editing, retiring, reactivating, deleting), the keys found and not registered, a user's keys, the caller's
/// own keys, the page's strip, a server's keys, and removing keys (the dialog's preview, the removal, offboarding, the history).
/// Admin, audited; <see cref="MineAsync"/> for every signed-in user, the server's keys for CanWrite. It names and assigns keys and
/// removes them where a server allows it; it never deploys one (§2.2).
/// </summary>
public sealed class SshKeyService(PnmtdDbContext db, AgentHub hub, SshKeyRemovals removals, IHttpContextAccessor accessor, IOptionsMonitor<AgentOptions> options)
{
    public const int MaxKeysPerRequest = 100;
    public const int MaxPasteChars = 512 * 1024;
    public const int MaxRemovalItems = 500;

    /// <summary>A key not used this long anywhere (as far as PNMT knows: since it was first seen) is <c>unused180</c>.</summary>
    public static readonly TimeSpan Unused = TimeSpan.FromDays(180);

    public static readonly string[] Filters = ["weak", "unused180", "ownerDeactivated", "retiredStillAuthorized"];

    /// <summary>A key with its owner; built by member initialisation, so queries can still filter and sort on it.</summary>
    private sealed class KeyRow
    {
        public required SshKeyEntity Key { get; init; }

        public string? Username { get; init; }

        public string? DisplayName { get; init; }

        public bool? UserActive { get; init; }
    }

    private sealed record Server(Guid SensorId, Ref Ref, string Hostname, AgentPlatform Platform, AgentRightsSummary? Rights, IReadOnlyList<string> Capabilities,
        bool Revoked, bool Connected);

    private IQueryable<KeyRow> Keys() =>
        from k in db.SshKeys.AsNoTracking()
        join u in db.Users.AsNoTracking() on k.UserId equals u.Id into users
        from u in users.DefaultIfEmpty()
        select new KeyRow { Key = k, Username = u == null ? null : u.Username, DisplayName = u == null ? null : u.DisplayName, UserActive = u == null ? null : (bool?)u.IsActive };

    private async Task<Dictionary<Guid, Server>> ServersAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0)
        {
            return new Dictionary<Guid, Server>();
        }
        var rows = await db.Agents.AsNoTracking().Where(a => list.Contains(a.SensorId))
            .Select(a => new { a.SensorId, Name = a.Sensor.Parent.Name + " / " + a.Sensor.Name, a.Hostname, a.Platform, a.Rights, a.Capabilities, a.RevokedAt })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.SensorId, r => new Server(r.SensorId, new Ref(r.SensorId, r.Name), r.Hostname, r.Platform, ServerRights.Read(r.Rights),
            r.Capabilities, r.RevokedAt is not null, hub.IsConnected(r.SensorId)));
    }

    private static Server Unknown(Guid id) => new(id, new Ref(id, "?"), "?", AgentPlatform.Linux, null, [], false, false);

    private static SshKeyOwner Owner(KeyRow row) => new(row.Key.UserId, row.Username, row.DisplayName, row.Key.Owner, row.Username ?? row.Key.Owner ?? "",
        row.UserActive == false, row.Key.UserId is null && row.Key.Owner?.StartsWith(SshKeyRegistry.FormerUserPrefix, StringComparison.Ordinal) == true);

    private static string Describe(KeyRow row) =>
        AgentAccessService.KeyName(row.Key.Fingerprint, new SshKeyName(row.Key.Name, row.Username ?? row.Key.Owner, row.Key.State == SshKeyState.Retired));

    private static SshKeyAuthorizationItem AuthorizationItem(SshKeyAuthorizationEntity a, Server s)
    {
        var why = SshKeyRegistry.CannotRemove(s.Rights, s.Capabilities, s.Revoked, a.Account, a.Removable, a.NotRemovable, a.Protected, a.ProtectedBecause);
        return new SshKeyAuthorizationItem(a.Id, s.Ref, s.Hostname, s.Connected, a.Account, a.Path, a.Line, a.Fingerprint, a.Type, a.Bits, a.Comment,
            SshKeyAuthorizations.Options(a), a.Notes, a.ClusterWide, a.Protected, a.ProtectedBecause, a.FirstSeenAt, a.LastUsedAt, a.LastUsedFrom, why is null, why);
    }

    private static SshKeyLastUse? LastUse(IEnumerable<SshKeyAuthorizationEntity> places, IReadOnlyDictionary<Guid, Server> servers) =>
        places.Where(a => a.LastUsedAt is not null).MaxBy(a => a.LastUsedAt) is { } last
            ? new SshKeyLastUse(last.LastUsedAt!.Value, servers.GetValueOrDefault(last.SensorId)?.Ref ?? new Ref(last.SensorId, "?"), last.Account, last.LastUsedFrom)
            : null;

    /// <summary>Not used for 180 days anywhere, as far as PNMT knows: its last use, or when it was first seen without one.</summary>
    private static bool IsUnused(IReadOnlyCollection<SshKeyAuthorizationEntity> places, DateTime now) =>
        places.Count > 0 && (places.Max(a => a.LastUsedAt) ?? places.Min(a => a.FirstSeenAt)) < now - Unused;

    private static SshKeyItem Item(KeyRow row, IReadOnlyCollection<SshKeyAuthorizationEntity> places, IReadOnlyDictionary<Guid, Server> servers, DateTime now)
    {
        var k = row.Key;
        var weakness = SshKeys.Weakness(k.Type, k.Bits);
        var notes = new List<string>();
        if (weakness is not null)
        {
            notes.Add("weak");
        }
        if (IsUnused(places, now))
        {
            notes.Add("unused180");
        }
        if (row.UserActive == false)
        {
            notes.Add("ownerDeactivated");
        }
        if (k.State == SshKeyState.Retired && places.Count > 0)
        {
            notes.Add("retiredStillAuthorized");
        }
        return new SshKeyItem(k.Id, k.Name, k.Fingerprint, k.Type, k.Bits, k.Comment, Owner(row), k.Kind, k.Note, k.State, k.RetiredAt, k.RetiredByName,
            places.Select(a => a.SensorId).Distinct().Count(), places.Select(a => (a.SensorId, a.Account)).Distinct().Count(), LastUse(places, servers), notes, weakness,
            k.CreatedAt, k.CreatedByName, k.UpdatedAt, k.UpdatedByName);
    }

    /// <summary>The items of these keys, with their authorizations.</summary>
    private async Task<List<SshKeyItem>> ItemsAsync(IReadOnlyCollection<KeyRow> rows, CancellationToken ct)
    {
        var fingerprints = rows.Select(r => r.Key.Fingerprint).ToList();
        var places = await db.SshKeyAuthorizations.AsNoTracking().Where(a => fingerprints.Contains(a.Fingerprint)).ToListAsync(ct);
        var servers = await ServersAsync(places.Select(a => a.SensorId), ct);
        var byKey = places.ToLookup(a => a.Fingerprint, StringComparer.Ordinal);
        var now = Clock.UtcNow;
        return rows.Select(r => Item(r, byKey[r.Key.Fingerprint].ToList(), servers, now)).ToList();
    }

    /// <summary>A search: a pasted public key or a fingerprint finds that key; anything else is a substring of the name, fingerprint, comment or owner.</summary>
    private static Func<string, string?[], bool> Matcher(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return (_, _) => true;
        }
        if (SshKeys.ParseLine(search).Key is { } pasted)
        {
            return (fingerprint, _) => fingerprint == pasted.Fingerprint;
        }
        if (SshKeys.NormalizeFingerprint(search) is { } exact)
        {
            return (fingerprint, _) => fingerprint == exact;
        }
        return (fingerprint, texts) => Text.Contains(fingerprint, search) || texts.Any(t => Text.Contains(t, search));
    }

    // --- the register (§6.4) --------------------------------------------------------------------------------------

    public async Task<Paged<SshKeyItem>> ListAsync(SshKeyQuery query, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "name", "name", "owner", "kind", "state", "lastused", "servers", "created");
        new ApiValidator().Check(query.Filter is null || Filters.Contains(query.Filter), "filter", $"One of: {string.Join(", ", Filters)}.").ThrowIfInvalid();
        var q = Keys();
        if (query.Kind is { } kind)
        {
            q = q.Where(r => r.Key.Kind == kind);
        }
        if (query.State is { } state)
        {
            q = q.Where(r => r.Key.State == state);
        }
        if (Guid.TryParse(query.Owner, out var ownerId))
        {
            q = q.Where(r => r.Key.UserId == ownerId);
        }
        else if (Text.Pattern(query.Owner) is { } owner)
        {
            q = q.Where(r => EF.Functions.ILike(r.Key.Owner ?? "", owner) || EF.Functions.ILike(r.Username ?? "", owner) || EF.Functions.ILike(r.DisplayName ?? "", owner));
        }
        var match = Matcher(query.Search);
        var rows = (await q.ToListAsync(ct)).Where(r => match(r.Key.Fingerprint, [r.Key.Name, r.Key.Comment, r.Key.Owner, r.Username, r.DisplayName])).ToList();
        var items = (await ItemsAsync(rows, ct)).Where(i => query.Filter is not { } filter || i.Notes.Contains(filter));
        IOrderedEnumerable<SshKeyItem> sorted = field switch
        {
            "owner" => items.OrderBy(i => i.Owner.Name, StringComparer.OrdinalIgnoreCase),
            "kind" => items.OrderBy(i => i.Kind),
            "state" => items.OrderBy(i => i.State),
            "lastused" => items.OrderBy(i => i.LastUse?.At ?? DateTime.MinValue),
            "servers" => items.OrderBy(i => i.Servers),
            "created" => items.OrderBy(i => i.CreatedAt),
            _ => items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
        var list = (descending ? sorted.Reverse() : sorted.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        return Paging.Page(list, query);
    }

    public async Task<SshKeyDetail> DetailAsync(Guid id, CancellationToken ct)
    {
        var row = await Keys().SingleOrDefaultAsync(r => r.Key.Id == id, ct) ?? throw ApiException.NotFound("SSH key");
        var fingerprint = row.Key.Fingerprint;
        var places = await db.SshKeyAuthorizations.AsNoTracking().Where(a => a.Fingerprint == fingerprint).ToListAsync(ct);
        var now = Clock.UtcNow;
        var since = now.AddDays(-30);
        var logins = await db.AgentLogins.AsNoTracking()
            .Where(l => l.KeyFingerprint == fingerprint && l.Outcome == LogonOutcome.Success && (l.LastAt ?? l.At) >= since)
            .OrderByDescending(l => l.LastAt ?? l.At).Take(50).ToListAsync(ct);
        var servers = await ServersAsync(places.Select(a => a.SensorId).Concat(logins.Select(l => l.SensorId)), ct);
        var item = Item(row, places, servers, now);

        // Its history: the audit's lines that name it, the servers' change lines about it, its removals.
        var auditTypes = new[] { AuthEvents.SshKeyRegistered, AuthEvents.SshKeyChanged, AuthEvents.SshKeyRetired, AuthEvents.SshKeyDeleted, AuthEvents.SshKeyRemoved };
        var audit = await db.AuthLog.AsNoTracking().Where(l => auditTypes.Contains(l.EventType) && l.Detail != null && l.Detail.Contains(fingerprint))
            .OrderByDescending(l => l.CreatedAt).Take(100).ToListAsync(ct);
        var key = JsonSerializer.Serialize(new { Key = fingerprint }, AgentJson.Options);
        var changes = await db.AgentChanges.AsNoTracking().Where(c => c.Section == SectionNames.SshKeys && c.Details != null && EF.Functions.JsonContains(c.Details, key))
            .OrderByDescending(c => c.At).Take(100).ToListAsync(ct);
        var changeServers = await ServersAsync(changes.Select(c => c.SensorId), ct);
        var history = audit.Select(l => new SshKeyHistoryItem(l.CreatedAt, "audit", l.Detail ?? l.EventType, l.Username, null))
            .Concat(changes.Select(c => new SshKeyHistoryItem(c.At, "change", c.Summary, null, changeServers.GetValueOrDefault(c.SensorId)?.Ref)))
            .OrderByDescending(h => h.At).Take(100).ToList();
        var removalRows = await db.SshKeyRemovals.AsNoTracking().Where(r => r.Fingerprint == fingerprint).OrderByDescending(r => r.RequestedAt).Take(50).ToListAsync(ct);
        return new SshKeyDetail(item, SshKeyRegistry.OpenSsh(row.Key.Type, row.Key.PublicKey, row.Key.Comment),
            places.Select(a => a.Comment).Append(row.Key.Comment).OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
            places.OrderBy(a => servers.GetValueOrDefault(a.SensorId)?.Ref.Name).ThenBy(a => a.Account).ThenBy(a => a.Path)
                .Select(a => AuthorizationItem(a, servers.GetValueOrDefault(a.SensorId) ?? Unknown(a.SensorId))).ToList(),
            logins.Select(l => new SshKeyLoginItem(l.Id, servers.GetValueOrDefault(l.SensorId)?.Ref ?? new Ref(l.SensorId, "?"), l.User, l.Kind, l.At, l.LastAt, l.Count,
                l.SourceAddress)).ToList(),
            history,
            await RemovalItemsAsync(removalRows, ct));
    }

    /// <summary>The parse preview of <i>Add keys…</i>: computed with PNMTD.Lib's parser as the text is typed. Stores and logs nothing.</summary>
    public async Task<SshKeyParseResult> ParseAsync(SshKeyParseRequest request, CancellationToken ct)
    {
        var text = request.Text ?? "";
        new ApiValidator().Check(text.Length <= MaxPasteChars, "text", $"At most {MaxPasteChars / 1024} KB.").ThrowIfInvalid();
        var lines = SshKeys.ParseMany(text);
        if (SshKeys.ContainsPrivateKey(text) || lines.Any(l => l.Problem == SshKeyLineProblem.PrivateKey))
        {
            var line = lines.FirstOrDefault(l => l.Problem == SshKeyLineProblem.PrivateKey).Line;
            return new SshKeyParseResult([new SshKeyParseLine(line > 0 ? line : 1, null, null, null, null, null, null, null, null, false, 0, SshKeyLineProblem.PrivateKey,
                ProblemText(SshKeyLineProblem.PrivateKey))], 0, 1, true);
        }
        var fingerprints = lines.Select(l => l.Key?.Fingerprint).OfType<string>().Distinct().ToList();
        var registered = await db.SshKeys.AsNoTracking().Where(k => fingerprints.Contains(k.Fingerprint)).Select(k => new { k.Fingerprint, k.Id, k.Name })
            .ToDictionaryAsync(k => k.Fingerprint, ct);
        var servers = (await db.SshKeyAuthorizations.AsNoTracking().Where(a => fingerprints.Contains(a.Fingerprint)).Select(a => new { a.Fingerprint, a.SensorId })
            .Distinct().ToListAsync(ct)).GroupBy(a => a.Fingerprint).ToDictionary(g => g.Key, g => g.Count());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        var result = new List<SshKeyParseLine>();
        foreach (var (number, key, problem) in lines)
        {
            if (key is null)
            {
                result.Add(new SshKeyParseLine(number, null, null, null, null, null, null, null, null, false, 0, problem, ProblemText(problem)));
                continue;
            }
            var known = registered.GetValueOrDefault(key.Fingerprint);
            result.Add(new SshKeyParseLine(number, index++, key.Fingerprint, key.Type, key.Bits, key.Comment, SshKeys.Weakness(key.Type, key.Bits), key.Comment,
                known is null ? null : new Ref(known.Id, known.Name), !seen.Add(key.Fingerprint), servers.GetValueOrDefault(key.Fingerprint), SshKeyLineProblem.None, null));
        }
        return new SshKeyParseResult(result, result.Count(l => l.Fingerprint is not null && l.Registered is null && !l.Duplicate),
            result.Count(l => l.Problem != SshKeyLineProblem.None || l.Registered is not null || l.Duplicate), false);
    }

    private static string? ProblemText(SshKeyLineProblem problem) => problem switch
    {
        SshKeyLineProblem.PrivateKey => "A private key: refused, and nothing of it is stored. Paste the public key (the .pub file); a private key that was copied is best replaced.",
        SshKeyLineProblem.TooLong => $"Longer than OpenSSH's limit of {SshKeys.MaxLineChars / 1024} KB: not a public key.",
        SshKeyLineProblem.Unreadable => "Not a public key: a type (ssh-ed25519, ecdsa-sha2-…, ssh-rsa) and its Base64, as in id_ed25519.pub.",
        _ => null,
    };

    /// <summary>The private key refusal (§6.6): before anything is stored or logged, and without repeating the text.</summary>
    private static ApiException PrivateKey() => new(StatusCodes.Status400BadRequest, "private_key",
        "This is a private key: nothing was stored. Paste the public key (the .pub file); a private key that was copied is best replaced.",
        new Dictionary<string, string[]> { ["publicKeys"] = ["A private key: refused."] });

    /// <summary>Registers one or more pasted keys (§6.4), each with its name, all with one owner, kind and note. Refused as a whole when a line is.</summary>
    public async Task<IReadOnlyList<SshKeyItem>> RegisterAsync(SshKeyRegisterRequest request, CancellationToken ct)
    {
        var text = request.PublicKeys ?? "";
        if (SshKeys.ContainsPrivateKey(text))
        {
            throw PrivateKey();
        }
        var v = new ApiValidator().Check(text.Length <= MaxPasteChars, "publicKeys", $"At most {MaxPasteChars / 1024} KB.");
        v.ThrowIfInvalid();
        var lines = SshKeys.ParseMany(text);
        if (lines.Any(l => l.Problem == SshKeyLineProblem.PrivateKey))
        {
            throw PrivateKey();
        }
        foreach (var line in lines.Where(l => l.Key is null))
        {
            v.Add("publicKeys", $"Line {line.Line}: {ProblemText(line.Problem)}");
        }
        var keys = lines.Where(l => l.Key is not null).Select(l => (l.Line, Key: l.Key!)).ToList();
        v.Check(keys.Count > 0 || lines.Any(l => l.Key is null), "publicKeys", "No public key in the text: paste one or more lines as in id_ed25519.pub.")
            .Check(keys.Count <= MaxKeysPerRequest, "publicKeys", $"At most {MaxKeysPerRequest} keys at once.");
        foreach (var (line, key) in keys)
        {
            var first = keys.First(k => k.Key.Fingerprint == key.Fingerprint);
            if (first.Line != line)
            {
                v.Add("publicKeys", $"Line {line}: the same key as line {first.Line}.");
            }
        }
        var fingerprints = keys.Select(k => k.Key.Fingerprint).Distinct().ToList();
        var registered = await db.SshKeys.AsNoTracking().Where(k => fingerprints.Contains(k.Fingerprint)).Select(k => new { k.Fingerprint, k.Name }).ToListAsync(ct);
        foreach (var known in registered)
        {
            var line = keys.First(k => k.Key.Fingerprint == known.Fingerprint).Line;
            v.Add("publicKeys", $"Line {line}: {SshKeys.Short(known.Fingerprint)} is registered already, as \"{known.Name}\".");
        }
        var (userId, owner) = await ValidateOwnerAsync(v, request.UserId, request.Owner, ct);
        v.Check(Enum.IsDefined(request.Kind), "kind", "Person or Service.")
            .Check(request.Note is null || request.Note.Length <= 2000, "note", "At most 2000 characters.");
        var names = new List<string>();
        for (var i = 0; i < keys.Count; i++)
        {
            var name = Text.Trimmed(request.Names is { } given && i < given.Count ? given[i] : null) ?? NameFrom(request.Name, keys[i].Key, i, keys.Count);
            v.Check(name is { Length: >= 1 and <= 200 }, $"names.{i}", $"Line {keys[i].Line}: a name of 1 to 200 characters (\"Philipp, laptop\").");
            names.Add(name ?? "");
        }
        v.ThrowIfInvalid();

        var now = Clock.UtcNow;
        var (_, by) = UdpViews.User(accessor);
        var ownerName = userId is { } u ? await db.Users.Where(x => x.Id == u).Select(x => x.Username).SingleAsync(ct) : owner;
        var created = new List<SshKeyEntity>();
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i].Key;
            var entity = new SshKeyEntity
            {
                Id = Guid.NewGuid(), Fingerprint = key.Fingerprint, Type = key.Type, Bits = key.Bits, PublicKey = key.Key, Comment = Cuts.CutOrNull(key.Comment, 500),
                Name = names[i], UserId = userId, Owner = owner, Kind = request.Kind, Note = Text.Trimmed(request.Note), State = SshKeyState.Active,
                CreatedAt = now, CreatedByName = by, UpdatedAt = now, UpdatedByName = by,
            };
            db.SshKeys.Add(entity);
            created.Add(entity);
            UdpViews.Audit(db, accessor, AuthEvents.SshKeyRegistered,
                Cuts.Cut($"{entity.Name} ({ownerName}, {entity.Kind.ToString().ToLowerInvariant()}): {entity.Type} {entity.Fingerprint} registered", 1000));
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, "A key was registered at the same moment by someone else: reload and try again.");
        }
        var ids = created.Select(c => c.Id).ToList();
        var rows = await Keys().Where(r => ids.Contains(r.Key.Id)).ToListAsync(ct);
        return (await ItemsAsync(rows, ct)).OrderBy(i => ids.IndexOf(i.Id)).ToList();
    }

    /// <summary>A name from the template: <c>{comment}</c> and <c>{n}</c> replaced; a plain name for several keys numbered.</summary>
    private static string? NameFrom(string? template, SshPublicKey key, int index, int count)
    {
        var name = Text.Trimmed(template);
        if (name is null)
        {
            return null;
        }
        if (name.Contains("{comment}", StringComparison.Ordinal) || name.Contains("{n}", StringComparison.Ordinal))
        {
            return name.Replace("{comment}", key.Comment ?? SshKeys.Short(key.Fingerprint), StringComparison.Ordinal)
                .Replace("{n}", (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal).Trim();
        }
        return count > 1 ? $"{name} ({index + 1})" : name;
    }

    /// <summary>A user or a text, one of them (§6.4).</summary>
    private async Task<(Guid? UserId, string? Owner)> ValidateOwnerAsync(ApiValidator v, Guid? userId, string? owner, CancellationToken ct)
    {
        var text = Text.Trimmed(owner);
        if (userId is not null && text is not null)
        {
            v.Add("owner", "A PNMT user or a text, not both.");
        }
        else if (userId is null && text is null)
        {
            v.Add("owner", "A key needs an owner: a PNMT user, or a text for keys of nobody in PNMT (a service, a contractor, a vendor's support).");
        }
        if (text is { Length: > 200 })
        {
            v.Add("owner", "At most 200 characters.");
        }
        if (userId is { } id && !await db.Users.AnyAsync(u => u.Id == id, ct))
        {
            v.Add("userId", "No such user.");
        }
        return (userId, userId is null ? text : null);
    }

    /// <summary>The name, owner, kind and note; the key itself never changes. Audited.</summary>
    public async Task<SshKeyDetail> UpdateAsync(Guid id, SshKeyUpdateRequest request, CancellationToken ct)
    {
        var key = await db.SshKeys.SingleOrDefaultAsync(k => k.Id == id, ct) ?? throw ApiException.NotFound("SSH key");
        var name = Text.Trimmed(request.Name);
        var v = new ApiValidator().Check(name is { Length: >= 1 and <= 200 }, "name", "A name of 1 to 200 characters (\"Philipp, laptop\").");
        var (userId, owner) = await ValidateOwnerAsync(v, request.UserId, request.Owner, ct);
        v.Check(Enum.IsDefined(request.Kind), "kind", "Person or Service.")
            .Check(request.Note is null || request.Note.Length <= 2000, "note", "At most 2000 characters.")
            .ThrowIfInvalid();
        var before = await OwnerNameAsync(key.UserId, key.Owner, ct);
        var after = await OwnerNameAsync(userId, owner, ct);
        var what = new List<string>();
        if (key.Name != name)
        {
            what.Add($"name {key.Name} → {name}");
        }
        if (before != after)
        {
            what.Add($"owner {before} → {after}");
        }
        if (key.Kind != request.Kind)
        {
            what.Add($"kind {key.Kind} → {request.Kind}");
        }
        if (key.Note != Text.Trimmed(request.Note))
        {
            what.Add("note changed");
        }
        if (what.Count > 0)
        {
            key.Name = name!;
            key.UserId = userId;
            key.Owner = owner;
            key.Kind = request.Kind;
            key.Note = Text.Trimmed(request.Note);
            key.UpdatedAt = Clock.UtcNow;
            key.UpdatedByName = UdpViews.User(accessor).Name;
            UdpViews.Audit(db, accessor, AuthEvents.SshKeyChanged, Cuts.Cut($"{key.Name} ({key.Fingerprint}): {string.Join("; ", what)}", 1000));
            await db.SaveChangesAsync(ct);
        }
        return await DetailAsync(id, ct);
    }

    private async Task<string?> OwnerNameAsync(Guid? userId, string? owner, CancellationToken ct) =>
        userId is { } id ? await db.Users.Where(u => u.Id == id).Select(u => u.Username).SingleOrDefaultAsync(ct) : owner;

    /// <summary>
    /// Retires a key (§6.4): a lost laptop, a key replaced, a colleague leaving. It stays where it is authorized (flagged there, with
    /// <i>Remove from every server…</i>), and appearing anywhere again raises 465. Audited.
    /// </summary>
    public async Task<SshKeyDetail> RetireAsync(Guid id, CancellationToken ct)
    {
        var key = await db.SshKeys.SingleOrDefaultAsync(k => k.Id == id, ct) ?? throw ApiException.NotFound("SSH key");
        if (key.State != SshKeyState.Retired)
        {
            var by = UdpViews.User(accessor).Name;
            key.State = SshKeyState.Retired;
            key.RetiredAt = Clock.UtcNow;
            key.RetiredByName = by;
            key.UpdatedAt = key.RetiredAt.Value;
            key.UpdatedByName = by;
            var servers = await db.SshKeyAuthorizations.Where(a => a.Fingerprint == key.Fingerprint).Select(a => a.SensorId).Distinct().CountAsync(ct);
            UdpViews.Audit(db, accessor, AuthEvents.SshKeyRetired,
                Cuts.Cut($"{key.Name} ({key.Fingerprint}) retired{(servers > 0 ? $"; still authorized on {servers} servers" : "")}", 1000));
            await db.SaveChangesAsync(ct);
        }
        return await DetailAsync(id, ct);
    }

    public async Task<SshKeyDetail> ReactivateAsync(Guid id, CancellationToken ct)
    {
        var key = await db.SshKeys.SingleOrDefaultAsync(k => k.Id == id, ct) ?? throw ApiException.NotFound("SSH key");
        if (key.State != SshKeyState.Active)
        {
            key.State = SshKeyState.Active;
            key.RetiredAt = null;
            key.RetiredByName = null;
            key.UpdatedAt = Clock.UtcNow;
            key.UpdatedByName = UdpViews.User(accessor).Name;
            UdpViews.Audit(db, accessor, AuthEvents.SshKeyChanged, Cuts.Cut($"{key.Name} ({key.Fingerprint}) reactivated", 1000));
            await db.SaveChangesAsync(ct);
        }
        return await DetailAsync(id, ct);
    }

    /// <summary>Deleting a registration (§6.4) makes the key unregistered again: it stays authorized wherever it is, and new appearances raise 465. Audited.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var key = await db.SshKeys.SingleOrDefaultAsync(k => k.Id == id, ct) ?? throw ApiException.NotFound("SSH key");
        var servers = await db.SshKeyAuthorizations.Where(a => a.Fingerprint == key.Fingerprint).Select(a => a.SensorId).Distinct().CountAsync(ct);
        db.SshKeys.Remove(key);
        UdpViews.Audit(db, accessor, AuthEvents.SshKeyDeleted,
            Cuts.Cut($"{key.Name} ({key.Fingerprint}) deleted: unregistered again{(servers > 0 ? $", still authorized on {servers} servers" : "")}", 1000));
        await db.SaveChangesAsync(ct);
    }

    // --- found on servers, users, mine, the strip -----------------------------------------------------------------

    /// <summary>Keys found on servers and not registered (§6.4's work list), by fingerprint, with every place they are authorized.</summary>
    public async Task<Paged<SshKeyUnregisteredItem>> UnregisteredAsync(SshKeyUnregisteredQuery query, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "-firstseen", "firstseen", "lastused", "servers", "type");
        new ApiValidator().Check(query.Filter is null or "weak" or "unused180", "filter", "weak or unused180.").ThrowIfInvalid();
        var places = await db.SshKeyAuthorizations.AsNoTracking().Where(a => !db.SshKeys.Any(k => k.Fingerprint == a.Fingerprint)).ToListAsync(ct);
        var servers = await ServersAsync(places.Select(a => a.SensorId), ct);
        var match = Matcher(query.Search);
        var now = Clock.UtcNow;
        var items = places.GroupBy(a => a.Fingerprint, StringComparer.Ordinal)
            .Where(g => query.SensorId is not { } sensor || g.Any(a => a.SensorId == sensor))
            .Where(g => match(g.Key, [.. g.Select(a => a.Comment), .. g.Select(a => a.Account), .. g.Select(a => servers.GetValueOrDefault(a.SensorId)?.Ref.Name)]))
            .Select(g =>
            {
                var list = g.ToList();
                var first = list[0];
                var weakness = SshKeys.Weakness(first.Type, first.Bits);
                var notes = list.SelectMany(a => a.Notes).Distinct(StringComparer.Ordinal).ToList();
                if (IsUnused(list, now))
                {
                    notes.Add("unused180");
                }
                var publicKey = list.FirstOrDefault(a => a.PublicKey.Length > 0) is { } withKey ? SshKeyRegistry.OpenSsh(withKey.Type, withKey.PublicKey, withKey.Comment) : null;
                return new SshKeyUnregisteredItem(g.Key, first.Type, first.Bits, publicKey,
                    list.Select(a => a.Comment).OfType<string>().Distinct(StringComparer.Ordinal).ToList(), list.Select(a => a.SensorId).Distinct().Count(),
                    list.Select(a => (a.SensorId, a.Account)).Distinct().Count(),
                    list.OrderBy(a => servers.GetValueOrDefault(a.SensorId)?.Ref.Name).ThenBy(a => a.Account)
                        .Select(a => AuthorizationItem(a, servers.GetValueOrDefault(a.SensorId) ?? Unknown(a.SensorId))).ToList(),
                    list.Min(a => a.FirstSeenAt), LastUse(list, servers), notes, weakness);
            })
            .Where(i => query.Filter switch { "weak" => i.Weakness is not null, "unused180" => i.Notes.Contains("unused180"), _ => true });
        IOrderedEnumerable<SshKeyUnregisteredItem> sorted = field switch
        {
            "lastused" => items.OrderBy(i => i.LastUse?.At ?? DateTime.MinValue),
            "servers" => items.OrderBy(i => i.Servers),
            "type" => items.OrderBy(i => i.Type, StringComparer.Ordinal),
            _ => items.OrderBy(i => i.FirstSeenAt),
        };
        return Paging.Page((descending ? sorted.Reverse() : sorted.ThenBy(i => i.Fingerprint, StringComparer.Ordinal)).ToList(), query);
    }

    private async Task<SshKeyUserRef> UserAsync(Guid userId, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new SshKeyUserRef(u.Id, u.Username, u.DisplayName, u.IsActive)).SingleOrDefaultAsync(ct)
        ?? throw ApiException.NotFound("User");

    /// <summary>A user's keys and where each is authorized (§6.4): for <i>Admin → Users</i> and offboarding.</summary>
    public async Task<SshKeyUserKeys> UserKeysAsync(Guid userId, CancellationToken ct)
    {
        var user = await UserAsync(userId, ct);
        var rows = await Keys().Where(r => r.Key.UserId == userId).OrderBy(r => r.Key.Name).ToListAsync(ct);
        var fingerprints = rows.Select(r => r.Key.Fingerprint).ToList();
        var places = await db.SshKeyAuthorizations.AsNoTracking().Where(a => fingerprints.Contains(a.Fingerprint)).ToListAsync(ct);
        var servers = await ServersAsync(places.Select(a => a.SensorId), ct);
        var byKey = places.ToLookup(a => a.Fingerprint, StringComparer.Ordinal);
        var now = Clock.UtcNow;
        return new SshKeyUserKeys(user, rows.Select(r =>
        {
            var list = byKey[r.Key.Fingerprint].ToList();
            return new SshKeyWithAuthorizations(Item(r, list, servers, now),
                list.OrderBy(a => servers.GetValueOrDefault(a.SensorId)?.Ref.Name).ThenBy(a => a.Account)
                    .Select(a => AuthorizationItem(a, servers.GetValueOrDefault(a.SensorId) ?? Unknown(a.SensorId))).ToList());
        }).ToList(), places.Count);
    }

    /// <summary>The caller's own keys (§6.4, every role): names, where each is authorized (server and account), last use. Read-only.</summary>
    public async Task<IReadOnlyList<SshKeyMineItem>> MineAsync(CancellationToken ct)
    {
        if (accessor.HttpContext?.User.UserId() is not { } me)
        {
            return [];
        }
        var keys = await db.SshKeys.AsNoTracking().Where(k => k.UserId == me).OrderBy(k => k.Name).ToListAsync(ct);
        var fingerprints = keys.Select(k => k.Fingerprint).ToList();
        var places = await db.SshKeyAuthorizations.AsNoTracking().Where(a => fingerprints.Contains(a.Fingerprint))
            .Select(a => new { a.Fingerprint, a.SensorId, a.Account, a.LastUsedAt }).ToListAsync(ct);
        var servers = await ServersAsync(places.Select(a => a.SensorId), ct);
        return keys.Select(k =>
        {
            var mine = places.Where(a => a.Fingerprint == k.Fingerprint).ToList();
            return new SshKeyMineItem(k.Id, k.Name, k.Fingerprint, k.Type, k.Bits, k.Comment, k.Kind, k.State,
                mine.Select(a => new SshKeyMinePlace(servers.GetValueOrDefault(a.SensorId)?.Ref.Name ?? "?", a.Account, a.LastUsedAt))
                    .OrderBy(p => p.Server, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Account, StringComparer.Ordinal).ToList(),
                mine.Max(a => a.LastUsedAt));
        }).ToList();
    }

    /// <summary>The page's strip (§11.4).</summary>
    public async Task<SshKeySummary> SummaryAsync(CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var keys = await db.SshKeys.AsNoTracking().Select(k => new { k.Fingerprint, k.State, k.UserId, k.Type, k.Bits }).ToListAsync(ct);
        var places = await db.SshKeyAuthorizations.AsNoTracking().Select(a => new { a.Fingerprint, a.Type, a.Bits, a.LastUsedAt, a.FirstSeenAt }).ToListAsync(ct);
        var registered = keys.Select(k => k.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var authorized = places.GroupBy(a => a.Fingerprint, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var deactivated = await db.Users.AsNoTracking().Where(u => !u.IsActive).Select(u => new SshKeyUserRef(u.Id, u.Username, u.DisplayName, u.IsActive))
            .ToDictionaryAsync(u => u.Id, ct);
        var deactivatedKeys = keys.Where(k => k.UserId is { } u && deactivated.ContainsKey(u) && authorized.ContainsKey(k.Fingerprint)).ToList();
        var weak = keys.Select(k => (k.Fingerprint, k.Type, k.Bits)).Concat(places.Select(a => (a.Fingerprint, a.Type, a.Bits)))
            .Where(k => SshKeys.Weakness(k.Type, k.Bits) is not null).Select(k => k.Fingerprint).Distinct().Count();
        var unused = keys.Count(k => authorized.TryGetValue(k.Fingerprint, out var list)
            && (list.Max(a => a.LastUsedAt) ?? list.Min(a => a.FirstSeenAt)) < now - Unused);
        return new SshKeySummary(keys.Count, keys.Count(k => k.State == SshKeyState.Active), keys.Count(k => k.State == SshKeyState.Retired),
            authorized.Keys.Count(f => !registered.Contains(f)), keys.Count(k => k.State == SshKeyState.Retired && authorized.ContainsKey(k.Fingerprint)),
            deactivatedKeys.Count,
            deactivatedKeys.GroupBy(k => k.UserId!.Value).Select(g => new SshKeyDeactivatedUser(deactivated[g.Key], g.Count(), g.Sum(k => authorized[k.Fingerprint].Count)))
                .OrderBy(u => u.User.Username, StringComparer.OrdinalIgnoreCase).ToList(),
            weak, unused, await db.SshKeyRemovals.CountAsync(r => r.State == SshKeyRemovalState.Pending, ct));
    }

    // --- a server's keys (§6.3) ----------------------------------------------------------------------------------

    /// <summary>A Linux server's <i>SSH keys</i> tab: its last ssh-keys section with each key's registration, last use and removability.</summary>
    public async Task<SshKeyServerView> ServerKeysAsync(Guid sensorId, CancellationToken ct)
    {
        var agent = await db.Agents.AsNoTracking().Where(a => a.SensorId == sensorId && AgentPlatformInfo.SensorTypes.Contains(a.Sensor.Type))
            .Select(a => new { a.Platform }).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("Server");
        if (agent.Platform != AgentPlatform.Linux)
        {
            throw ApiException.NotFound("Linux server (SSH keys are read on Linux servers)");
        }
        var server = (await ServersAsync([sensorId], ct))[sensorId];
        var serverWhy = SshKeyRegistry.ServerCannotRemove(server.Rights, server.Capabilities, server.Revoked);
        var users = server.Rights?.KeyRemovalUsers ?? [];
        var row = await db.AgentSections.AsNoTracking().SingleOrDefaultAsync(s => s.SensorId == sensorId && s.Section == SectionNames.SshKeys, ct);
        var section = row is null ? null : AgentSections.Parse<SshKeysSection>(row.Content);
        if (section is null)
        {
            return new SshKeyServerView(sensorId, row?.CollectedAt, null, [], [], 0, false, row is null ? [] : ["PNMT could not read the stored ssh-keys section."],
                server.Connected, serverWhy is null, serverWhy, users, 0, 0, 0);
        }
        var places = await db.SshKeyAuthorizations.AsNoTracking().Where(a => a.SensorId == sensorId).ToListAsync(ct);
        var byPlace = places.GroupBy(a => (a.Account, a.Path, a.Fingerprint)).ToDictionary(g => g.Key, g => g.First());
        var fingerprints = section.Files.SelectMany(f => f.Keys).Select(k => k.Fingerprint).Distinct().ToList();
        var registered = (await Keys().Where(r => fingerprints.Contains(r.Key.Fingerprint)).ToListAsync(ct)).ToDictionary(r => r.Key.Fingerprint, StringComparer.Ordinal);
        var files = section.Files.Select(f => new SshKeyServerFile(f.Account, f.Path, f.Target, f.Exists, f.Owner, f.Group, f.Mode, f.ModifiedAt?.UtcDateTime, f.ClusterWide,
            f.Problems, f.InvalidLines, f.Removable, f.NotRemovable, f.Keys.Select(k =>
            {
                var place = byPlace.GetValueOrDefault((Cuts.Cut(f.Account, 256), Cuts.Cut(f.Path, 1024), k.Fingerprint));
                var why = SshKeyRegistry.CannotRemove(server.Rights, server.Capabilities, server.Revoked, f.Account, f.Removable, f.NotRemovable, k.Protected, k.ProtectedBecause);
                var registration = registered.GetValueOrDefault(k.Fingerprint) is { } r
                    ? new SshKeyRegistration(r.Key.Id, r.Key.Name, Owner(r), r.Key.Kind, r.Key.State)
                    : null;
                return new SshKeyServerKey(k.Line, k.Type, k.Bits, k.Fingerprint, k.Comment, k.Options, k.Notes, k.Protected, k.ProtectedBecause,
                    SshKeyRegistry.OpenSsh(k.Type, k.Key, k.Comment), registration, place?.FirstSeenAt, place?.LastUsedAt, place?.LastUsedFrom, why is null, why,
                    SshKeys.Weakness(k.Type, k.Bits));
            }).ToList())).ToList();
        var keys = section.Files.SelectMany(f => f.Keys).DistinctBy(k => k.Fingerprint).ToList();
        return new SshKeyServerView(sensorId, row!.CollectedAt, section.Sshd, section.CaKeys, files, section.TotalKeys, section.Truncated, section.Notes, server.Connected,
            serverWhy is null, serverWhy, users,
            keys.Count(k => !registered.ContainsKey(k.Fingerprint)), keys.Count(k => registered.GetValueOrDefault(k.Fingerprint)?.Key.State == SshKeyState.Retired),
            keys.Count(k => SshKeys.Weakness(k.Type, k.Bits) is not null));
    }

    // --- removing keys (§6.5) -----------------------------------------------------------------------------------

    private sealed record Planned(SshKeyRemovalTarget Item, Server? Server, SshKeyAuthorizationEntity? Place, KeyRow? Key, string KeyName, string? Refused,
        List<string> Warnings, int KeysLeft, IReadOnlyList<string> Nodes);

    private static string PlaceKey(Guid sensorId, string account, string path, string fingerprint) => $"{sensorId}\n{account}\n{path}\n{fingerprint}";

    /// <summary>Each item checked as the dialog shows it (§6.5 step 2): allowed or not (and why), and its warnings.</summary>
    private async Task<List<Planned>> PlanAsync(IReadOnlyList<SshKeyRemovalTarget> items, CancellationToken ct)
    {
        var sensorIds = items.Select(i => i.SensorId).Distinct().ToList();
        var servers = await ServersAsync(sensorIds, ct);
        var places = await db.SshKeyAuthorizations.AsNoTracking().Where(a => sensorIds.Contains(a.SensorId)).ToListAsync(ct);
        var fingerprints = items.Select(i => i.Fingerprint).Distinct().ToList();
        var keys = (await Keys().Where(r => fingerprints.Contains(r.Key.Fingerprint)).ToListAsync(ct)).ToDictionary(r => r.Key.Fingerprint, StringComparer.Ordinal);
        var removing = items.Select(i => PlaceKey(i.SensorId, i.Account, i.Path, i.Fingerprint)).ToHashSet(StringComparer.Ordinal);
        var passwordOff = new Dictionary<Guid, bool>();
        var clusters = new Dictionary<Guid, (string? Name, IReadOnlyList<string> Nodes)>();
        var now = Clock.UtcNow;
        var days = removals.PendingDays;
        var planned = new List<Planned>();
        foreach (var item in items)
        {
            var server = servers.GetValueOrDefault(item.SensorId);
            var place = places.FirstOrDefault(a => a.SensorId == item.SensorId && a.Account == item.Account && a.Path == item.Path && a.Fingerprint == item.Fingerprint);
            var key = keys.GetValueOrDefault(item.Fingerprint);
            var name = SshKeyRegistry.Describe(item.Fingerprint, place?.Type, place?.Comment, key is null ? null
                : new RegisteredKey(key.Key.Id, key.Key.Fingerprint, key.Key.Name, key.Key.UserId, key.Username, key.DisplayName, key.UserActive, key.Key.Owner, key.Key.Kind, key.Key.State));
            if (server is null || server.Platform != AgentPlatform.Linux)
            {
                planned.Add(new Planned(item, server, null, key, name, "No such Linux server.", [], 0, []));
                continue;
            }
            if (place is null)
            {
                planned.Add(new Planned(item, server, null, key, name,
                    $"PNMT's last ssh-keys section from {server.Hostname} doesn't have this key in {item.Account}'s {item.Path}: it may be gone already.", [], 0, []));
                continue;
            }
            var why = SshKeyRegistry.CannotRemove(server.Rights, server.Capabilities, server.Revoked, place.Account, place.Removable, place.NotRemovable, place.Protected,
                place.ProtectedBecause);
            var left = places.Count(a => a.SensorId == item.SensorId && a.Account == item.Account && !removing.Contains(PlaceKey(a.SensorId, a.Account, a.Path, a.Fingerprint)));
            var warnings = new List<string>();
            IReadOnlyList<string> nodes = [];
            if (left == 0)
            {
                warnings.Add($"{item.Account} would have no key left on {server.Hostname}.");
                if (item.Account == "root")
                {
                    if (!passwordOff.TryGetValue(item.SensorId, out var off))
                    {
                        passwordOff[item.SensorId] = off = await PasswordLoginOffAsync(item.SensorId, ct);
                    }
                    if (off)
                    {
                        warnings.Add($"root would have no key left on {server.Hostname} and password login is off: this may lock everyone out of root.");
                    }
                }
            }
            if (place.LastUsedAt is { } used && used > now.AddHours(-24))
            {
                warnings.Add($"Used {Durations.Text(now - used)} ago{(place.LastUsedFrom is { } from ? $" from {from}" : "")}: its sessions stay open.");
            }
            if (place.ClusterWide)
            {
                if (!clusters.TryGetValue(item.SensorId, out var cluster))
                {
                    var system = await AgentSections.ReadAsync<LinuxSystemSection>(db, item.SensorId, SectionNames.System, ct);
                    clusters[item.SensorId] = cluster = (system?.Proxmox?.ClusterName, system?.Proxmox?.Nodes ?? []);
                }
                nodes = cluster.Nodes;
                warnings.Add($"The file is cluster-wide (Proxmox VE): the key goes from every node of {cluster.Name ?? "the cluster"}"
                    + (cluster.Nodes.Count > 0 ? $": {string.Join(", ", cluster.Nodes)}." : "."));
            }
            if (!server.Connected && why is null)
            {
                warnings.Add($"{server.Hostname} is offline: the removal waits until it connects, at most {days} days.");
            }
            planned.Add(new Planned(item, server, place, key, name, why, warnings, left, nodes));
        }
        return planned;
    }

    /// <summary>Whether root can't log in with a password (§6.5 step 2): sshd's settings from the remote-access section, else the ssh-keys section's.</summary>
    private async Task<bool> PasswordLoginOffAsync(Guid sensorId, CancellationToken ct)
    {
        static bool NoPassword(string? permitRootLogin) => permitRootLogin is "prohibit-password" or "without-password" or "forced-commands-only" or "no";
        if ((await AgentSections.ReadAsync<LinuxRemoteAccessSection>(db, sensorId, SectionNames.RemoteAccess, ct))?.Sshd is { } sshd)
        {
            return NoPassword(sshd.PermitRootLogin) || (!sshd.PasswordAuthentication && !sshd.KbdInteractiveAuthentication);
        }
        var keys = await AgentSections.ReadAsync<SshKeysSection>(db, sensorId, SectionNames.SshKeys, ct);
        return keys?.Sshd.PermitRootLogin is not { } permit || NoPassword(permit);
    }

    private static SshKeyRemovalPreview Preview(List<Planned> planned) => new(
        planned.Select(p => new SshKeyRemovalPreviewItem(p.Item, p.Server?.Ref, p.Server?.Ref.Name ?? "?", p.Server?.Connected ?? false, p.KeyName, p.Key?.Key.Id,
            p.Key is null ? null : p.Key.Username ?? p.Key.Key.Owner, p.Place?.Comment, p.Refused is null, p.Refused, p.Warnings, p.Place?.ClusterWide ?? false, p.Nodes,
            p.Place?.LastUsedAt, p.KeysLeft)).ToList(),
        planned.Count(p => p.Refused is null), planned.Count(p => p.Refused is not null),
        ["Sessions that already use a removed key stay open: PNMT doesn't end them.", "Restoring a removed key is local: pnmt-agent keys restore <backup>, as root on the server."],
        planned.Count);

    private static List<SshKeyRemovalTarget> Distinct(IReadOnlyList<SshKeyRemovalTarget>? items) =>
        (items ?? []).Where(i => i is not null).Select(i => i with { Account = i.Account?.Trim() ?? "", Path = i.Path?.Trim() ?? "", Fingerprint = SshKeys.NormalizeFingerprint(i.Fingerprint) ?? i.Fingerprint?.Trim() ?? "" })
            .DistinctBy(i => PlaceKey(i.SensorId, i.Account, i.Path, i.Fingerprint)).ToList();

    /// <summary>The remove dialog's preview for these items.</summary>
    public async Task<SshKeyRemovalPreview> PreviewAsync(SshKeyRemovalPreviewRequest request, CancellationToken ct)
    {
        var items = Distinct(request.Items);
        new ApiValidator().Check(items.Count is >= 1 and <= MaxRemovalItems, "items", $"Between 1 and {MaxRemovalItems} keys.").ThrowIfInvalid();
        return Preview(await PlanAsync(items, ct));
    }

    /// <summary>"Remove from every server…": every place a registered key is authorized, as the dialog's preview.</summary>
    public async Task<SshKeyRemovalPreview> KeyRemovalItemsAsync(Guid id, CancellationToken ct)
    {
        var fingerprint = await db.SshKeys.AsNoTracking().Where(k => k.Id == id).Select(k => k.Fingerprint).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("SSH key");
        return Preview(await PlanAsync(await PlacesAsync([fingerprint], ct), ct));
    }

    /// <summary>Offboarding's preview: every place any key of the user is authorized.</summary>
    public async Task<SshKeyRemovalPreview> UserRemovalItemsAsync(Guid userId, CancellationToken ct)
    {
        await UserAsync(userId, ct);
        var fingerprints = await db.SshKeys.AsNoTracking().Where(k => k.UserId == userId).Select(k => k.Fingerprint).ToListAsync(ct);
        return Preview(await PlanAsync(await PlacesAsync(fingerprints, ct), ct));
    }

    private async Task<List<SshKeyRemovalTarget>> PlacesAsync(IReadOnlyCollection<string> fingerprints, CancellationToken ct) =>
        await db.SshKeyAuthorizations.AsNoTracking().Where(a => fingerprints.Contains(a.Fingerprint)).OrderBy(a => a.SensorId).ThenBy(a => a.Account).ThenBy(a => a.Path)
            .Select(a => new SshKeyRemovalTarget(a.SensorId, a.Account, a.Path, a.Fingerprint)).ToListAsync(ct);

    /// <summary>
    /// Removes keys (§6.5): <c>confirm</c> is the number of items, as the Admin typed it. Each is recorded: refused where the server
    /// doesn't allow it, else pending, and sent at once to a connected agent that announced keys.remove.v1 (an offline server gets it
    /// when it connects). Audited.
    /// </summary>
    public async Task<SshKeyRemoveResult> RemoveAsync(SshKeyRemoveRequest request, CancellationToken ct)
    {
        var items = Distinct(request.Items);
        new ApiValidator()
            .Check(items.Count is >= 1 and <= MaxRemovalItems, "items", $"Between 1 and {MaxRemovalItems} keys.")
            .Check(Enum.IsDefined(request.Reason), "reason", "Server, Key, Retirement, Offboarding or Unregistered.")
            .Check(request.Confirm == items.Count, "confirm", $"Type the number of keys to remove: {items.Count}.")
            .ThrowIfInvalid();
        return await RemoveCoreAsync(items, request.Reason, ct);
    }

    private async Task<SshKeyRemoveResult> RemoveCoreAsync(IReadOnlyList<SshKeyRemovalTarget> items, SshKeyRemovalReason reason, CancellationToken ct)
    {
        var planned = await PlanAsync(items, ct);
        var now = Clock.UtcNow;
        var batch = Guid.NewGuid();
        var (userId, by) = UdpViews.User(accessor);
        var rows = planned.Select(p => new SshKeyRemovalEntity
        {
            Id = Guid.NewGuid(), BatchId = batch, SensorId = p.Server?.SensorId, ServerName = Cuts.Cut(p.Server?.Ref.Name ?? p.Item.SensorId.ToString(), 500),
            Account = Cuts.Cut(p.Item.Account, 256), Path = Cuts.Cut(p.Item.Path, 1024), Fingerprint = Cuts.Cut(p.Item.Fingerprint, 100), KeyName = Cuts.Cut(p.KeyName, 500),
            RequestedAt = now, RequestedBy = userId, RequestedByName = by, Reason = reason,
            State = p.Refused is null ? SshKeyRemovalState.Pending : SshKeyRemovalState.Refused, Message = Cuts.CutOrNull(p.Refused, 2000),
            AppliedAt = p.Refused is null ? null : now,
        }).ToList();
        db.SshKeyRemovals.AddRange(rows);
        var allowed = rows.Count(r => r.State == SshKeyRemovalState.Pending);
        UdpViews.Audit(db, accessor, AuthEvents.SshKeyRemoved, Cuts.Cut(
            $"{rows.Count} {(rows.Count == 1 ? "removal" : "removals")} asked for ({reason.ToString().ToLowerInvariant()}, batch {batch}): {allowed} to send, "
            + $"{rows.Count - allowed} refused: " + string.Join("; ", rows.Select(r => $"{r.KeyName} ({r.Fingerprint}) from {SshKeyRegistry.File(r.Account, r.Path)} on {r.ServerName}")), 1000));
        await db.SaveChangesAsync(ct);

        foreach (var group in rows.Where(r => r.State == SshKeyRemovalState.Pending).GroupBy(r => r.SensorId!.Value))
        {
            if (hub.Connection(group.Key) is { } connection && connection.Capabilities.Contains(AgentCapabilities.KeysRemove))
            {
                await removals.SendAsync(connection, group.Select(r => r.Id).ToList(), ct);
            }
        }
        var ids = rows.Select(r => r.Id).ToList();
        var stored = await db.SshKeyRemovals.AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        var result = (await RemovalItemsAsync(stored, ct)).OrderBy(r => ids.IndexOf(r.Id)).ToList();
        return new SshKeyRemoveResult(batch, result, result.Count(r => r.State is not (SshKeyRemovalState.Pending or SshKeyRemovalState.Refused)),
            result.Count(r => r.State == SshKeyRemovalState.Pending), result.Count(r => r.State == SshKeyRemovalState.Refused));
    }

    /// <summary>
    /// Offboarding (§6.4): every key of the user retired, and removed from every server and account it is authorized on (one removal
    /// per server, account, file and key). <c>confirm</c> is the number of removals. Audited.
    /// </summary>
    public async Task<SshKeyOffboardResult> OffboardAsync(Guid userId, SshKeyOffboardRequest request, CancellationToken ct)
    {
        var user = await UserAsync(userId, ct);
        var keys = await db.SshKeys.Where(k => k.UserId == userId).ToListAsync(ct);
        var items = await PlacesAsync(keys.Select(k => k.Fingerprint).ToList(), ct);
        new ApiValidator().Check(request.Confirm == items.Count, "confirm", $"Type the number of keys to remove: {items.Count}.").ThrowIfInvalid();
        var now = Clock.UtcNow;
        var by = UdpViews.User(accessor).Name;
        var retired = 0;
        foreach (var key in keys.Where(k => k.State != SshKeyState.Retired))
        {
            key.State = SshKeyState.Retired;
            key.RetiredAt = now;
            key.RetiredByName = by;
            key.UpdatedAt = now;
            key.UpdatedByName = by;
            retired++;
            UdpViews.Audit(db, accessor, AuthEvents.SshKeyRetired, Cuts.Cut($"{key.Name} ({key.Fingerprint}) retired: offboarding of {user.Username}", 1000));
        }
        await db.SaveChangesAsync(ct);
        var removal = items.Count == 0 ? new SshKeyRemoveResult(Guid.Empty, [], 0, 0, 0) : await RemoveCoreAsync(items, SshKeyRemovalReason.Offboarding, ct);
        return new SshKeyOffboardResult(user, retired, removal);
    }

    /// <summary>The removals' history (§11.4's <i>Removals</i>), newest first.</summary>
    public async Task<Paged<SshKeyRemovalItem>> RemovalsAsync(SshKeyRemovalQuery query, CancellationToken ct)
    {
        var q = db.SshKeyRemovals.AsNoTracking();
        if (query.SensorId is { } sensor)
        {
            q = q.Where(r => r.SensorId == sensor);
        }
        if (!string.IsNullOrWhiteSpace(query.Fingerprint))
        {
            var fingerprint = SshKeys.NormalizeFingerprint(query.Fingerprint) ?? query.Fingerprint.Trim();
            q = q.Where(r => r.Fingerprint == fingerprint);
        }
        if (query.State is { } state)
        {
            q = q.Where(r => r.State == state);
        }
        if (query.Reason is { } reason)
        {
            q = q.Where(r => r.Reason == reason);
        }
        if (query.BatchId is { } batch)
        {
            q = q.Where(r => r.BatchId == batch);
        }
        if (Guid.TryParse(query.Owner, out var ownerId))
        {
            var owned = await SshKeyRegistry.OwnedBy(db, ownerId, null).ToListAsync(ct);
            q = q.Where(r => owned.Contains(r.Fingerprint));
        }
        else if (Text.Pattern(query.Owner) is { } owner)
        {
            var owned = await SshKeyRegistry.OwnedBy(db, null, owner).ToListAsync(ct);
            q = q.Where(r => owned.Contains(r.Fingerprint) || EF.Functions.ILike(r.KeyName ?? "", owner));
        }
        if (Text.Pattern(query.Search) is { } search)
        {
            q = q.Where(r => EF.Functions.ILike(r.KeyName ?? "", search) || EF.Functions.ILike(r.ServerName, search) || EF.Functions.ILike(r.Account, search)
                || EF.Functions.ILike(r.Path, search) || EF.Functions.ILike(r.Fingerprint, search));
        }
        var page = await Paging.PageAsync(q.OrderByDescending(r => r.RequestedAt).ThenBy(r => r.ServerName), query, ct);
        return new Paged<SshKeyRemovalItem>(await RemovalItemsAsync(page.Items, ct), page.Page, page.PageSize, page.TotalCount);
    }

    private async Task<List<SshKeyRemovalItem>> RemovalItemsAsync(IReadOnlyCollection<SshKeyRemovalEntity> rows, CancellationToken ct)
    {
        var fingerprints = rows.Select(r => r.Fingerprint).Distinct().ToList();
        var keys = await db.SshKeys.AsNoTracking().Where(k => fingerprints.Contains(k.Fingerprint)).Select(k => new { k.Fingerprint, k.Id }).ToDictionaryAsync(k => k.Fingerprint, k => k.Id, ct);
        var sensorIds = rows.Select(r => r.SensorId).OfType<Guid>().Distinct().ToList();
        var existing = (await db.Sensors.AsNoTracking().Where(s => sensorIds.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        return rows.Select(r => new SshKeyRemovalItem(r.Id, r.BatchId, r.SensorId is { } s && existing.Contains(s) ? new Ref(s, r.ServerName) : null, r.ServerName, r.Account,
            r.Path, r.Fingerprint, r.KeyName, keys.TryGetValue(r.Fingerprint, out var id) ? id : null, r.RequestedAt, r.RequestedByName, r.Reason, r.State, r.SentAt,
            r.LinesRemoved, r.Backup, r.Message, r.AppliedAt)).ToList();
    }
}
