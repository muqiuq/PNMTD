using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Agents;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Windows;
using PNMTD.Windows.Scripts;

namespace PNMTD.Services.Domain;

/// <summary>
/// PowerShell scripts in the API (docs/windows-sensor-plan.md §7): the library with a version per change, parsed on an agent;
/// run-as accounts (names only, never a password); runs on servers, signed and with secrets encrypted to each agent; the
/// history, with arguments an Admin can redact. All Admin, all audited.
/// </summary>
public sealed class WindowsScriptService(
    PnmtdDbContext db,
    AgentHub hub,
    AgentKeyStore keys,
    ScriptResults results,
    IHttpContextAccessor accessor)
{
    public const int MaxScriptChars = 256 * 1024;
    public const int DefaultTimeoutSeconds = 600;
    public const int MaxTimeoutSeconds = 3600;

    // --- the library (§7.1) ---------------------------------------------------------------------------------

    public async Task<Paged<WindowsScriptItem>> ScriptsAsync(PageQuery query, CancellationToken ct)
    {
        var q = db.WindowsScripts.AsNoTracking();
        if (Text.Pattern(query.Search) is { } pattern)
        {
            q = q.Where(s => EF.Functions.ILike(s.Name, pattern) || EF.Functions.ILike(s.Description ?? "", pattern));
        }
        var page = await Paging.PageAsync(q.OrderBy(s => s.Name), query, ct);
        var ids = page.Items.Select(s => s.Id).ToList();
        var current = await db.WindowsScriptVersions.AsNoTracking().Where(v => ids.Contains(v.ScriptId))
            .Join(db.WindowsScripts.Where(s => ids.Contains(s.Id)), v => new { v.ScriptId, v.Number }, s => new { ScriptId = s.Id, Number = s.CurrentVersion }, (v, _) => v)
            .ToDictionaryAsync(v => v.ScriptId, ct);
        return new Paged<WindowsScriptItem>(page.Items.Select(s =>
        {
            var version = current.GetValueOrDefault(s.Id);
            var parsed = Parse(version?.Parsed);
            return new WindowsScriptItem(s.Id, s.Name, s.Description, s.CurrentVersion, version?.ParseState ?? ScriptParseState.Pending,
                parsed?.Parameters.Count ?? 0, parsed?.Findings.Count ?? 0, s.UpdatedAt);
        }).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<WindowsScriptDetail> ScriptAsync(Guid id, CancellationToken ct)
    {
        var script = await db.WindowsScripts.AsNoTracking().Include(s => s.Versions).SingleOrDefaultAsync(s => s.Id == id, ct)
            ?? throw ApiException.NotFound("Script");
        var current = script.Versions.Single(v => v.Number == script.CurrentVersion);
        return new WindowsScriptDetail(script.Id, script.Name, script.Description, script.CurrentVersion, current.Text, current.Hash, current.ParseState,
            Parse(current.Parsed), current.ParseError,
            script.Versions.OrderByDescending(v => v.Number).Select(v => new WindowsScriptVersionItem(v.Id, v.Number, v.Hash, v.CreatedAt, v.CreatedByName,
                v.ParseState, v.ParsedAt, v.ParsedOn, v.ParseError)).ToList(),
            hub.Connections.Any(c => c.Capabilities.Contains(AgentCapabilities.Parse)));
    }

    private static ParsedScript? Parse(string? json) => json is null ? null : JsonSerializer.Deserialize<ParsedScript>(json, AgentJson.Options);

    public async Task<WindowsScriptDetail> CreateAsync(WindowsScriptRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, null, ct);
        var now = Clock.UtcNow;
        var script = new WindowsScriptEntity
        {
            Id = Guid.NewGuid(), Name = request.Name.Trim(), Description = Text.Trimmed(request.Description), CreatedAt = now, UpdatedAt = now, CurrentVersion = 1,
        };
        script.Versions.Add(Version(script.Id, 1, request.Text));
        db.WindowsScripts.Add(script);
        UdpViews.Audit(db, accessor, AuthEvents.WindowsScriptChanged, $"script {script.Name} created (v1, {script.Versions[0].Hash[..12]})");
        await db.SaveChangesAsync(ct);
        await results.ParsePendingAsync(null, ct);
        return await ScriptAsync(script.Id, ct);
    }

    /// <summary>A new version when the text changed; the name and description change in place.</summary>
    public async Task<WindowsScriptDetail> UpdateAsync(Guid id, WindowsScriptRequest request, CancellationToken ct)
    {
        var script = await db.WindowsScripts.Include(s => s.Versions).SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Script");
        await ValidateAsync(request, id, ct);
        script.Name = request.Name.Trim();
        script.Description = Text.Trimmed(request.Description);
        script.UpdatedAt = Clock.UtcNow;
        var current = script.Versions.Single(v => v.Number == script.CurrentVersion);
        if (current.Text != request.Text)
        {
            var next = Version(script.Id, script.CurrentVersion + 1, request.Text);
            db.WindowsScriptVersions.Add(next);
            script.CurrentVersion = next.Number;
            UdpViews.Audit(db, accessor, AuthEvents.WindowsScriptChanged, $"script {script.Name}: v{next.Number} ({next.Hash[..12]})");
        }
        await db.SaveChangesAsync(ct);
        await results.ParsePendingAsync(null, ct);
        return await ScriptAsync(id, ct);
    }

    /// <summary>Parses the current version again, on a connected agent.</summary>
    public async Task<WindowsScriptDetail> ReparseAsync(Guid id, CancellationToken ct)
    {
        var script = await db.WindowsScripts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Script");
        await db.WindowsScriptVersions.Where(v => v.ScriptId == id && v.Number == script.CurrentVersion)
            .ExecuteUpdateAsync(u => u.SetProperty(v => v.ParseState, ScriptParseState.Pending), ct);
        await results.ParsePendingAsync(null, ct);
        return await ScriptAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var script = await db.WindowsScripts.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Script");
        db.WindowsScripts.Remove(script);
        UdpViews.Audit(db, accessor, AuthEvents.WindowsScriptChanged, $"script {script.Name} deleted (its runs stay in the history)");
        await db.SaveChangesAsync(ct);
    }

    private WindowsScriptVersionEntity Version(Guid scriptId, int number, string text) => new()
    {
        Id = Guid.NewGuid(), ScriptId = scriptId, Number = number, Text = text, Hash = ScriptHashes.Of(text), CreatedAt = Clock.UtcNow,
        CreatedByName = UdpViews.User(accessor).Name, ParseState = ScriptParseState.Pending,
    };

    private async Task ValidateAsync(WindowsScriptRequest request, Guid? id, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        var v = new ApiValidator()
            .Check(name is { Length: >= 1 and <= 200 }, "name", "The name must have 1 to 200 characters.")
            .Check(request.Description is null || request.Description.Length <= 2000, "description", "At most 2000 characters.")
            .Check(request.Text is { Length: >= 1 and <= MaxScriptChars }, "text", $"The script must have 1 to {MaxScriptChars} characters.");
        if (name is not null)
        {
            v.Check(!await db.WindowsScripts.AnyAsync(s => s.Name == name && s.Id != id, ct), "name", "Another script has this name.");
        }
        v.ThrowIfInvalid();
    }

    // --- run-as accounts (§7.4) -------------------------------------------------------------------------------

    public async Task<IReadOnlyList<WindowsRunAsItem>> RunAsListAsync(CancellationToken ct) =>
        await db.WindowsRunAs.AsNoTracking().OrderBy(r => r.Name)
            .Select(r => new WindowsRunAsItem(r.Id, r.Name, r.Kind, r.Account, r.Description, r.CreatedAt)).ToListAsync(ct);

    public async Task<WindowsRunAsItem> SaveRunAsAsync(Guid? id, WindowsRunAsRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        var account = Text.Trimmed(request.Account);
        var v = new ApiValidator()
            .Check(name is { Length: >= 1 and <= 200 }, "name", "The name must have 1 to 200 characters.")
            .Check(Enum.IsDefined(request.Kind), "kind", "Service, Stored, TypedPerRun or Gmsa.")
            .Check(request.Kind == RunAsKind.Service ? account is null : account is { Length: >= 1 and <= 256 }, "account",
                request.Kind == RunAsKind.Service ? "The service runs as LocalSystem: no account." : "DOMAIN\\user, at most 256 characters.")
            .Check(request.Kind != RunAsKind.Gmsa || account?.EndsWith('$') == true, "account", "A gMSA's name ends with $.")
            .Check(request.Description is null || request.Description.Length <= 1000, "description", "At most 1000 characters.");
        if (name is not null)
        {
            v.Check(!await db.WindowsRunAs.AnyAsync(r => r.Name == name && r.Id != id, ct), "name", "Another account has this name.");
        }
        v.ThrowIfInvalid();
        var row = id is { } existing
            ? await db.WindowsRunAs.SingleOrDefaultAsync(r => r.Id == existing, ct) ?? throw ApiException.NotFound("Run-as account")
            : new WindowsRunAsEntity { Id = Guid.NewGuid(), CreatedAt = Clock.UtcNow }.Also(r => db.WindowsRunAs.Add(r));
        row.Name = name!;
        row.Kind = request.Kind;
        row.Account = account;
        row.Description = Text.Trimmed(request.Description);
        UdpViews.Audit(db, accessor, AuthEvents.WindowsScriptChanged, $"run-as account {row.Name} ({row.Kind}{(account is null ? "" : $", {account}")}) saved");
        await db.SaveChangesAsync(ct);
        return new WindowsRunAsItem(row.Id, row.Name, row.Kind, row.Account, row.Description, row.CreatedAt);
    }

    public async Task DeleteRunAsAsync(Guid id, CancellationToken ct)
    {
        var row = await db.WindowsRunAs.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiException.NotFound("Run-as account");
        db.WindowsRunAs.Remove(row);
        UdpViews.Audit(db, accessor, AuthEvents.WindowsScriptChanged, $"run-as account {row.Name} deleted");
        await db.SaveChangesAsync(ct);
    }

    // --- runs (§7.3) ----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<WindowsScriptRunItem>> RunAsync(Guid scriptId, WindowsScriptRunRequest request, CancellationToken ct)
    {
        var script = await db.WindowsScripts.AsNoTracking().Include(s => s.Versions).SingleOrDefaultAsync(s => s.Id == scriptId, ct)
            ?? throw ApiException.NotFound("Script");
        var version = script.Versions.Single(v => v.Number == script.CurrentVersion);
        var parsed = Parse(version.Parsed);
        if (version.ParseState != ScriptParseState.Parsed || parsed is null)
        {
            throw ApiException.Conflict("parameters_pending", "The script's parameters aren't known yet: an agent parses it when one is connected.");
        }
        if (parsed.Errors.Count > 0)
        {
            throw ApiException.Conflict("syntax_errors", $"The script has syntax errors: {parsed.Errors[0].Message} (line {parsed.Errors[0].Line}).");
        }
        var runAs = await db.WindowsRunAs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.RunAsId, ct);
        var sensorIds = (request.SensorIds ?? []).Distinct().ToList();
        var arguments = request.Arguments ?? new Dictionary<string, JsonElement>();
        var secure = request.SecureArguments ?? new Dictionary<string, string>();
        var timeout = request.TimeoutSeconds ?? DefaultTimeoutSeconds;
        var shell = request.Shell ?? "powershell";
        var v = new ApiValidator()
            .Check(sensorIds.Count is >= 1 and <= 100, "sensorIds", "Between 1 and 100 servers.")
            .Check(runAs is not null, "runAsId", "No such run-as account.")
            .Check(runAs?.Kind != RunAsKind.TypedPerRun || !string.IsNullOrEmpty(request.Password), "password", "This account's password is typed per run.")
            .Check(timeout is >= 10 and <= MaxTimeoutSeconds, "timeoutSeconds", $"Between 10 and {MaxTimeoutSeconds} seconds.")
            .Check(shell is "powershell" or "pwsh", "shell", "powershell (Windows PowerShell 5.1) or pwsh.");
        foreach (var problem in ScriptArguments.Check(parsed, arguments, secure.Keys.ToList()))
        {
            v.Check(false, problem.Field, problem.Message);
        }
        var servers = await db.WindowsAgents.AsNoTracking().Where(a => sensorIds.Contains(a.SensorId))
            .Select(a => new { a.SensorId, a.EncryptionKey, Name = a.Sensor.Parent.Name + " / " + a.Sensor.Name }).ToListAsync(ct);
        v.Check(servers.Count == sensorIds.Count, "sensorIds", "Every server must be a Windows server.");
        v.ThrowIfInvalid();

        var (_, user) = UdpViews.User(accessor);
        var batch = Guid.NewGuid();
        var plain = JsonSerializer.Serialize(arguments, AgentJson.Options);
        var rows = new List<WindowsScriptRunEntity>();
        foreach (var server in servers)
        {
            var row = new WindowsScriptRunEntity
            {
                Id = Guid.NewGuid(), BatchId = batch, ScriptId = script.Id, ScriptName = script.Name, VersionNumber = version.Number, Hash = version.Hash,
                SensorId = server.SensorId, ServerName = server.Name, RunAsName = runAs!.Name, RunAsKind = runAs.Kind, Account = runAs.Account,
                Arguments = arguments.Count == 0 && secure.Count == 0 ? null : plain, RequestedBy = user, StartedAt = Clock.UtcNow, State = ScriptRunState.Sent,
                TimeoutSeconds = timeout,
            };
            db.WindowsScriptRuns.Add(row);
            rows.Add(row);
        }
        UdpViews.Audit(db, accessor, AuthEvents.WindowsScriptRun,
            $"script {script.Name} v{version.Number} on {string.Join(", ", servers.Select(s => s.Name))} as {runAs!.Name} ({runAs.Kind}), batch {batch}");
        await db.SaveChangesAsync(ct);

        foreach (var row in rows)
        {
            var server = servers.Single(s => s.SensorId == row.SensorId);
            var connection = hub.Connection(server.SensorId);
            if (connection is null)
            {
                Finish(row, ScriptRunState.Offline, "The agent is not connected.");
                continue;
            }
            if (!connection.Capabilities.Contains(AgentCapabilities.Scripts))
            {
                Finish(row, ScriptRunState.Refused, "Scripts are not allowed on this server: an administrator turns them on in the agent's window there.");
                continue;
            }
            if (server.EncryptionKey is null && (secure.Count > 0 || runAs.Kind == RunAsKind.TypedPerRun))
            {
                Finish(row, ScriptRunState.Refused, "The agent has no encryption key for secrets: update it.");
                continue;
            }
            var spec = new RunAsSpec(runAs.Kind, runAs.Account,
                runAs.Kind == RunAsKind.TypedPerRun ? AgentSecrets.Encrypt(server.EncryptionKey!, request.Password!, row.Id) : null);
            var encrypted = secure.ToDictionary(p => p.Key, p => AgentSecrets.Encrypt(server.EncryptionKey!, p.Value, row.Id));
            var message = new RunScriptRequest(row.Id, script.Name, version.Hash, version.Text, arguments, encrypted, spec, timeout, shell);
            try
            {
                var answer = await connection.RequestAsync(keys, AgentMessageTypes.RunScript, message, TimeSpan.FromSeconds(30), ct);
                if (AgentJson.Deserialize<Ack>(answer) is { Ok: false } refused)
                {
                    Finish(row, ScriptRunState.Refused, refused.Error ?? "Refused.");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Finish(row, ScriptRunState.Failed, "The agent did not answer within 30 s.");
            }
        }
        await db.SaveChangesAsync(ct);
        return rows.Select(Item).ToList();
    }

    private static void Finish(WindowsScriptRunEntity row, ScriptRunState state, string message)
    {
        row.State = state;
        row.Message = message;
        row.FinishedAt = Clock.UtcNow;
    }

    public async Task<Paged<WindowsScriptRunItem>> RunsAsync(WindowsScriptRunQuery query, CancellationToken ct)
    {
        var q = db.WindowsScriptRuns.AsNoTracking();
        if (query.ScriptId is { } script)
        {
            q = q.Where(r => r.ScriptId == script);
        }
        if (query.SensorId is { } sensor)
        {
            q = q.Where(r => r.SensorId == sensor);
        }
        if (query.BatchId is { } batch)
        {
            q = q.Where(r => r.BatchId == batch);
        }
        if (query.State is { } state)
        {
            q = q.Where(r => r.State == state);
        }
        var page = await Paging.PageAsync(q.OrderByDescending(r => r.StartedAt), query, ct);
        return new Paged<WindowsScriptRunItem>(page.Items.Select(Item).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<WindowsScriptRunDetail> RunDetailAsync(Guid id, CancellationToken ct)
    {
        var row = await db.WindowsScriptRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiException.NotFound("Run");
        return new WindowsScriptRunDetail(Item(row), row.Arguments, row.Output, row.Hash, row.TimeoutSeconds);
    }

    /// <summary>Removes a run's arguments from the history (§7.3). Audit-logged.</summary>
    public async Task<WindowsScriptRunDetail> RedactAsync(Guid id, CancellationToken ct)
    {
        var row = await db.WindowsScriptRuns.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiException.NotFound("Run");
        row.Arguments = null;
        row.Redacted = true;
        UdpViews.Audit(db, accessor, AuthEvents.WindowsScriptRun, $"run {id} of {row.ScriptName}: arguments redacted");
        await db.SaveChangesAsync(ct);
        return await RunDetailAsync(id, ct);
    }

    private static WindowsScriptRunItem Item(WindowsScriptRunEntity r) => new(r.Id, r.BatchId, r.ScriptId, r.ScriptName, r.VersionNumber,
        r.SensorId is { } s ? new Ref(s, r.ServerName) : null, r.ServerName, r.RunAsName, r.RunAsKind, r.Account, r.RequestedBy, r.StartedAt, r.FinishedAt, r.State,
        r.ExitCode, r.Message, r.DurationMs, r.Redacted);
}

internal static class WindowsScriptExtensions
{
    public static T Also<T>(this T value, Action<T> init)
    {
        init(value);
        return value;
    }
}
