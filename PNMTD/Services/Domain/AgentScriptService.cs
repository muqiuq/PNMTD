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

namespace PNMTD.Services.Domain;

/// <summary>
/// Scripts in the API (docs/windows-sensor-plan.md §7; docs/linux-sensor-plan.md §8): the library with a version per change,
/// parsed on an agent (PowerShell, until bash comes with L7); Windows' run-as accounts (names only, never a password) and the
/// scripts registered on a Windows server; runs on servers, signed and with secrets encrypted to each agent; the history,
/// with arguments an Admin can redact. All Admin, all audited.
/// </summary>
public sealed class AgentScriptService(
    PnmtdDbContext db,
    AgentHub hub,
    AgentKeyStore keys,
    AgentScriptResults results,
    IHttpContextAccessor accessor)
{
    public const int MaxScriptChars = 256 * 1024;
    public const int DefaultTimeoutSeconds = 600;
    public const int MaxTimeoutSeconds = 3600;

    // --- the library (§7.1) ---------------------------------------------------------------------------------

    public async Task<Paged<AgentScriptItem>> ScriptsAsync(PageQuery query, CancellationToken ct)
    {
        var q = db.AgentScripts.AsNoTracking();
        if (Text.Pattern(query.Search) is { } pattern)
        {
            q = q.Where(s => EF.Functions.ILike(s.Name, pattern) || EF.Functions.ILike(s.Description ?? "", pattern));
        }
        var page = await Paging.PageAsync(q.OrderBy(s => s.Name), query, ct);
        var ids = page.Items.Select(s => s.Id).ToList();
        var current = await db.AgentScriptVersions.AsNoTracking().Where(v => ids.Contains(v.ScriptId))
            .Join(db.AgentScripts.Where(s => ids.Contains(s.Id)), v => new { v.ScriptId, v.Number }, s => new { ScriptId = s.Id, Number = s.CurrentVersion }, (v, _) => v)
            .ToDictionaryAsync(v => v.ScriptId, ct);
        return new Paged<AgentScriptItem>(page.Items.Select(s =>
        {
            var version = current.GetValueOrDefault(s.Id);
            var parsed = Parse(version?.Parsed);
            return new AgentScriptItem(s.Id, s.Name, s.Description, s.Language, s.CurrentVersion, version?.ParseState ?? ScriptParseState.Pending,
                parsed?.Parameters.Count ?? 0, parsed?.Findings.Count ?? 0, s.UpdatedAt);
        }).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<AgentScriptDetail> ScriptAsync(Guid id, CancellationToken ct)
    {
        var script = await db.AgentScripts.AsNoTracking().Include(s => s.Versions).SingleOrDefaultAsync(s => s.Id == id, ct)
            ?? throw ApiException.NotFound("Script");
        var current = script.Versions.Single(v => v.Number == script.CurrentVersion);
        return new AgentScriptDetail(script.Id, script.Name, script.Description, script.Language, script.CurrentVersion, current.Text, current.Hash, current.ParseState,
            Parse(current.Parsed), current.ParseError,
            script.Versions.OrderByDescending(v => v.Number).Select(v => new AgentScriptVersionItem(v.Id, v.Number, v.Hash, v.CreatedAt, v.CreatedByName,
                v.ParseState, v.ParsedAt, v.ParsedOn, v.ParseError)).ToList(),
            results.ParserAvailable);
    }

    private static ParsedScript? Parse(string? json) => json is null ? null : JsonSerializer.Deserialize<ParsedScript>(json, AgentJson.Options);

    public async Task<AgentScriptDetail> CreateAsync(AgentScriptRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, null, ct);
        var now = Clock.UtcNow;
        var script = new AgentScriptEntity
        {
            Id = Guid.NewGuid(), Name = request.Name.Trim(), Description = Text.Trimmed(request.Description), Language = ScriptLanguage.PowerShell, CreatedAt = now,
            UpdatedAt = now, CurrentVersion = 1,
        };
        script.Versions.Add(Version(script.Id, 1, request.Text));
        db.AgentScripts.Add(script);
        UdpViews.Audit(db, accessor, AuthEvents.AgentScriptChanged, $"script {script.Name} created (v1, {script.Versions[0].Hash[..12]})");
        await db.SaveChangesAsync(ct);
        await results.ParsePendingAsync(null, ct);
        return await ScriptAsync(script.Id, ct);
    }

    /// <summary>A new version when the text changed; the name and description change in place.</summary>
    public async Task<AgentScriptDetail> UpdateAsync(Guid id, AgentScriptRequest request, CancellationToken ct)
    {
        var script = await db.AgentScripts.Include(s => s.Versions).SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Script");
        await ValidateAsync(request, id, ct);
        script.Name = request.Name.Trim();
        script.Description = Text.Trimmed(request.Description);
        script.UpdatedAt = Clock.UtcNow;
        var current = script.Versions.Single(v => v.Number == script.CurrentVersion);
        if (current.Text != request.Text)
        {
            var next = Version(script.Id, script.CurrentVersion + 1, request.Text);
            db.AgentScriptVersions.Add(next);
            script.CurrentVersion = next.Number;
            UdpViews.Audit(db, accessor, AuthEvents.AgentScriptChanged, $"script {script.Name}: v{next.Number} ({next.Hash[..12]})");
        }
        await db.SaveChangesAsync(ct);
        await results.ParsePendingAsync(null, ct);
        return await ScriptAsync(id, ct);
    }

    /// <summary>Parses the current version again, on a connected agent.</summary>
    public async Task<AgentScriptDetail> ReparseAsync(Guid id, CancellationToken ct)
    {
        var script = await db.AgentScripts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Script");
        await db.AgentScriptVersions.Where(v => v.ScriptId == id && v.Number == script.CurrentVersion)
            .ExecuteUpdateAsync(u => u.SetProperty(v => v.ParseState, ScriptParseState.Pending), ct);
        await results.ParsePendingAsync(null, ct);
        return await ScriptAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var script = await db.AgentScripts.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Script");
        db.AgentScripts.Remove(script);
        UdpViews.Audit(db, accessor, AuthEvents.AgentScriptChanged, $"script {script.Name} deleted (its runs stay in the history)");
        await db.SaveChangesAsync(ct);
    }

    private AgentScriptVersionEntity Version(Guid scriptId, int number, string text) => new()
    {
        Id = Guid.NewGuid(), ScriptId = scriptId, Number = number, Text = text, Hash = ScriptHashes.Of(text), CreatedAt = Clock.UtcNow,
        CreatedByName = UdpViews.User(accessor).Name, ParseState = ScriptParseState.Pending,
    };

    private async Task ValidateAsync(AgentScriptRequest request, Guid? id, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        var v = new ApiValidator()
            .Check(name is { Length: >= 1 and <= 200 }, "name", "The name must have 1 to 200 characters.")
            .Check(request.Description is null || request.Description.Length <= 2000, "description", "At most 2000 characters.")
            .Check(request.Text is { Length: >= 1 and <= MaxScriptChars }, "text", $"The script must have 1 to {MaxScriptChars} characters.");
        if (name is not null)
        {
            v.Check(!await db.AgentScripts.AnyAsync(s => s.Name == name && s.Id != id, ct), "name", "Another script has this name.");
        }
        v.ThrowIfInvalid();
    }

    // --- Windows' run-as accounts (§7.4) ---------------------------------------------------------------------

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
        UdpViews.Audit(db, accessor, AuthEvents.AgentScriptChanged, $"run-as account {row.Name} ({row.Kind}{(account is null ? "" : $", {account}")}) saved");
        await db.SaveChangesAsync(ct);
        return new WindowsRunAsItem(row.Id, row.Name, row.Kind, row.Account, row.Description, row.CreatedAt);
    }

    public async Task DeleteRunAsAsync(Guid id, CancellationToken ct)
    {
        var row = await db.WindowsRunAs.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiException.NotFound("Run-as account");
        db.WindowsRunAs.Remove(row);
        UdpViews.Audit(db, accessor, AuthEvents.AgentScriptChanged, $"run-as account {row.Name} deleted");
        await db.SaveChangesAsync(ct);
    }

    // --- runs (§7.3) ----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AgentScriptRunItem>> RunAsync(Guid scriptId, AgentScriptRunRequest request, CancellationToken ct)
    {
        var script = await db.AgentScripts.AsNoTracking().Include(s => s.Versions).SingleOrDefaultAsync(s => s.Id == scriptId, ct)
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
        // PowerShell runs on Windows servers; a script of another language on another platform comes with bash (L7).
        var servers = await db.Agents.AsNoTracking().Where(a => sensorIds.Contains(a.SensorId) && a.Platform == AgentPlatform.Windows)
            .Select(a => new { a.SensorId, a.EncryptionKey, Name = a.Sensor.Parent.Name + " / " + a.Sensor.Name }).ToListAsync(ct);
        v.Check(servers.Count == sensorIds.Count, "sensorIds", "Every server must be a Windows server: the script is PowerShell.");
        v.ThrowIfInvalid();

        var (_, user) = UdpViews.User(accessor);
        var batch = Guid.NewGuid();
        var plain = JsonSerializer.Serialize(arguments, AgentJson.Options);
        var rows = new List<AgentScriptRunEntity>();
        foreach (var server in servers)
        {
            var row = new AgentScriptRunEntity
            {
                Id = Guid.NewGuid(), BatchId = batch, ScriptId = script.Id, ScriptName = script.Name, VersionNumber = version.Number, Hash = version.Hash,
                SensorId = server.SensorId, ServerName = server.Name, RunAsName = runAs!.Name, RunAsKind = runAs.Kind, Account = runAs.Account,
                Arguments = arguments.Count == 0 && secure.Count == 0 ? null : plain, RequestedBy = user, StartedAt = Clock.UtcNow, State = ScriptRunState.Sent,
                TimeoutSeconds = timeout,
            };
            db.AgentScriptRuns.Add(row);
            rows.Add(row);
        }
        UdpViews.Audit(db, accessor, AuthEvents.AgentScriptRun,
            $"script {script.Name} v{version.Number} on {string.Join(", ", servers.Select(s => s.Name))} as {runAs!.Name} ({runAs.Kind}), batch {batch}");
        await db.SaveChangesAsync(ct);

        foreach (var row in rows)
        {
            var server = servers.Single(s => s.SensorId == row.SensorId);
            await SendAsync(row, server.EncryptionKey, version.Text, arguments, secure, runAs.Kind == RunAsKind.TypedPerRun ? request.Password : null, shell, ct);
        }
        await db.SaveChangesAsync(ct);
        return rows.Select(Item).ToList();
    }

    /// <summary>Sends a run to its server's agent, signed, with the secrets encrypted to it; a refusal or no answer finishes the row.</summary>
    private async Task SendAsync(AgentScriptRunEntity row, string? encryptionKey, string text, IReadOnlyDictionary<string, JsonElement> arguments,
        IReadOnlyDictionary<string, string> secure, string? password, string shell, CancellationToken ct)
    {
        var connection = row.SensorId is { } sensorId ? hub.Connection(sensorId) : null;
        if (connection is null)
        {
            Finish(row, ScriptRunState.Offline, "The agent is not connected.");
            return;
        }
        if (!connection.Capabilities.Contains(AgentCapabilities.Scripts))
        {
            Finish(row, ScriptRunState.Refused, "Scripts are not allowed on this server: an administrator turns them on in the agent's window there.");
            return;
        }
        if (encryptionKey is null && (secure.Count > 0 || password is not null))
        {
            Finish(row, ScriptRunState.Refused, "The agent has no encryption key for secrets: update it.");
            return;
        }
        var spec = new RunAsSpec(row.RunAsKind, row.Account, password is null ? null : AgentSecrets.Encrypt(encryptionKey!, password, row.Id));
        var encrypted = secure.ToDictionary(p => p.Key, p => AgentSecrets.Encrypt(encryptionKey!, p.Value, row.Id));
        var message = new RunScriptRequest(row.Id, row.ScriptName, row.Hash, text, arguments, encrypted, spec, row.TimeoutSeconds, shell);
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

    // --- scripts registered on a Windows server (§7.2) ----------------------------------------------------------

    /// <summary>The scripts registered on a server, from its last <c>scripts</c> section, with the library script of the same text.</summary>
    public async Task<WindowsLocalScripts> LocalScriptsAsync(Guid sensorId, CancellationToken ct)
    {
        var (collectedAt, section) = await LocalSectionAsync(sensorId, ct);
        if (section is null)
        {
            return new WindowsLocalScripts(null, false, false, [], []);
        }
        var hashes = section.Scripts.Select(s => s.Sha256).ToList();
        var library = await db.AgentScriptVersions.AsNoTracking().Where(v => hashes.Contains(v.Hash))
            .Join(db.AgentScripts, v => new { v.ScriptId, v.Number }, s => new { ScriptId = s.Id, Number = s.CurrentVersion }, (v, s) => new { v.Hash, s.Id, s.Name })
            .ToListAsync(ct);
        return new WindowsLocalScripts(collectedAt, section.Allowed, section.AnyScript, section.RunAs, section.Scripts.Select(s =>
        {
            var match = library.FirstOrDefault(l => l.Hash == s.Sha256);
            return new WindowsLocalScript(s, RunAsName(s), match?.Id, match?.Name);
        }).ToList());
    }

    /// <summary>
    /// Runs a script registered on the server, as its registered account: the text is the one the server reported (its hash is
    /// the registered one), the arguments are checked against its parse here and again on the agent.
    /// </summary>
    public async Task<AgentScriptRunItem> RunLocalAsync(Guid sensorId, WindowsLocalScriptRunRequest request, CancellationToken ct)
    {
        var server = await db.Agents.AsNoTracking().Where(a => a.SensorId == sensorId && a.Platform == AgentPlatform.Windows)
            .Select(a => new { a.SensorId, a.EncryptionKey, Name = a.Sensor.Parent.Name + " / " + a.Sensor.Name }).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Windows server");
        var (_, section) = await LocalSectionAsync(sensorId, ct);
        var script = section?.Scripts.FirstOrDefault(s => string.Equals(s.Name, request.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw ApiException.NotFound("Registered script");
        if (!script.Matches || script.Text is null)
        {
            throw ApiException.Conflict("script_changed", $"{script.Name}'s copy on the server no longer matches its registration: replace it in the agent's window.");
        }
        if (script.Parsed is null)
        {
            throw ApiException.Conflict("parameters_pending", $"The agent could not parse {script.Name}{(script.ParseError is { } e ? $": {e}" : "")}.");
        }
        if (script.Parsed.Errors.Count > 0)
        {
            throw ApiException.Conflict("syntax_errors", $"The script has syntax errors: {script.Parsed.Errors[0].Message} (line {script.Parsed.Errors[0].Line}).");
        }
        var arguments = request.Arguments ?? new Dictionary<string, JsonElement>();
        var secure = request.SecureArguments ?? new Dictionary<string, string>();
        var timeout = request.TimeoutSeconds ?? DefaultTimeoutSeconds;
        var shell = request.Shell ?? "powershell";
        var v = new ApiValidator()
            .Check(timeout is >= 10 and <= MaxTimeoutSeconds, "timeoutSeconds", $"Between 10 and {MaxTimeoutSeconds} seconds.")
            .Check(shell is "powershell" or "pwsh", "shell", "powershell (Windows PowerShell 5.1) or pwsh.");
        foreach (var problem in ScriptArguments.Check(script.Parsed, arguments, secure.Keys.ToList()))
        {
            v.Check(false, problem.Field, problem.Message);
        }
        v.ThrowIfInvalid();

        var (_, user) = UdpViews.User(accessor);
        var row = new AgentScriptRunEntity
        {
            Id = Guid.NewGuid(), BatchId = Guid.NewGuid(), ScriptId = null, ScriptName = script.Name, VersionNumber = 0, Hash = script.Sha256,
            SensorId = sensorId, ServerName = server.Name, RunAsName = RunAsName(script), RunAsKind = script.RunAs, Account = script.Account,
            Arguments = arguments.Count == 0 && secure.Count == 0 ? null : JsonSerializer.Serialize(arguments, AgentJson.Options), RequestedBy = user,
            StartedAt = Clock.UtcNow, State = ScriptRunState.Sent, TimeoutSeconds = timeout,
        };
        db.AgentScriptRuns.Add(row);
        UdpViews.Audit(db, accessor, AuthEvents.AgentScriptRun,
            $"script {script.Name} registered on {server.Name} ({script.Sha256[..12]}) as {row.RunAsName}, batch {row.BatchId}");
        await db.SaveChangesAsync(ct);
        await SendAsync(row, server.EncryptionKey, script.Text, arguments, secure, null, shell, ct);
        await db.SaveChangesAsync(ct);
        return Item(row);
    }

    private async Task<(DateTime? CollectedAt, ScriptsSection? Section)> LocalSectionAsync(Guid sensorId, CancellationToken ct)
    {
        var row = await db.AgentSections.AsNoTracking().SingleOrDefaultAsync(s => s.SensorId == sensorId && s.Section == SectionNames.Scripts, ct);
        return row is null ? (null, null) : (row.CollectedAt, AgentSections.Parse<ScriptsSection>(row.Content));
    }

    /// <summary>The account as the window names it: the service, a gMSA, or the account whose password is stored there.</summary>
    private static string RunAsName(RegisteredScript script) => script.RunAs switch
    {
        RunAsKind.Service => "this service (LocalSystem)",
        RunAsKind.Gmsa => $"{script.Account} (gMSA)",
        _ => $"{script.Account} (stored on the server)",
    };

    private static void Finish(AgentScriptRunEntity row, ScriptRunState state, string message)
    {
        row.State = state;
        row.Message = message;
        row.FinishedAt = Clock.UtcNow;
    }

    public async Task<Paged<AgentScriptRunItem>> RunsAsync(AgentScriptRunQuery query, CancellationToken ct)
    {
        var q = db.AgentScriptRuns.AsNoTracking();
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
        return new Paged<AgentScriptRunItem>(page.Items.Select(Item).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<AgentScriptRunDetail> RunDetailAsync(Guid id, CancellationToken ct)
    {
        var row = await db.AgentScriptRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiException.NotFound("Run");
        return new AgentScriptRunDetail(Item(row), row.Arguments, row.Output, row.Hash, row.TimeoutSeconds);
    }

    /// <summary>Removes a run's arguments from the history (§7.3). Audit-logged.</summary>
    public async Task<AgentScriptRunDetail> RedactAsync(Guid id, CancellationToken ct)
    {
        var row = await db.AgentScriptRuns.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiException.NotFound("Run");
        row.Arguments = null;
        row.Redacted = true;
        UdpViews.Audit(db, accessor, AuthEvents.AgentScriptRun, $"run {id} of {row.ScriptName}: arguments redacted");
        await db.SaveChangesAsync(ct);
        return await RunDetailAsync(id, ct);
    }

    private static AgentScriptRunItem Item(AgentScriptRunEntity r) => new(r.Id, r.BatchId, r.ScriptId, r.ScriptName, r.VersionNumber,
        r.SensorId is { } s ? new Ref(s, r.ServerName) : null, r.ServerName, r.RunAsName, r.RunAsKind, r.Account, r.RequestedBy, r.StartedAt, r.FinishedAt, r.State,
        r.ExitCode, r.Message, r.DurationMs, r.Redacted);
}

internal static class AgentScriptExtensions
{
    public static T Also<T>(this T value, Action<T> init)
    {
        init(value);
        return value;
    }
}
