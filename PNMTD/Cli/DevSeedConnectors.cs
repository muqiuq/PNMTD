using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PNMTD.Auth;
using PNMTD.Connectors;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Cli;

/// <summary>
/// 3rd Party Connectors in the demo data (docs/connectors-plan.md §15): <i>Shop: flush cache</i> (Windows, wait mode, a function, the
/// review on) and <i>Backup: start job</i> (Linux, ticket mode, a function); the callers <i>Shop back office</i> (both, 10.0.0.0/8) and
/// <i>Lab</i> (one, from anywhere, so the warning shows); and calls of each state, one queued behind a running one.
/// </summary>
internal static class DevSeedConnectors
{
    public const string ShopTools = """
        <#
        .SYNOPSIS
            Tools for the shop's web servers.
        #>
        [CmdletBinding()]
        param()
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'

        function Clear-ShopCache {
            <#
            .SYNOPSIS
                Empties a site's output cache.
            .PARAMETER Site
                The site, as named in IIS.
            #>
            [CmdletBinding()]
            param(
                [Parameter(Mandatory = $true)] [string] $Site,
                [ValidateSet('Pages', 'Images', 'All')] [string] $Scope = 'Pages',
                [switch] $DryRun
            )
            Write-Output "Flushing $Site ($Scope)"
        }

        function Restart-ShopPool { [CmdletBinding()] param([Parameter(Mandatory = $true)] [string] $Site) Restart-WebAppPool -Name $Site }
        """;

    public const string BackupTools = """
        #!/usr/bin/env bash
        # pnmt:
        #   synopsis: Tools for the backup server
        #   functions:
        #     start_job:
        #       synopsis: Starts a backup job now
        #       parameters:
        #         job: { type: string, mandatory: true, set: [nightly, weekly, monthly] }
        #     job_status:
        #       synopsis: Prints a job's last result
        #       parameters:
        #         job: { type: string, mandatory: true }
        set -euo pipefail

        start_job()  { systemctl start "backup-${PNMT_JOB}.service"; echo "started ${PNMT_JOB}"; }
        job_status() { systemctl show "backup-${PNMT_JOB}.service" -p Result --value; }

        if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
          echo "Call a function: start_job or job_status" >&2
          exit 2
        fi
        """;

    public static int Seed(PnmtdDbContext db, Dictionary<string, HostEntity> hosts, IConfiguration configuration, DateTime now)
    {
        var protector = new ConnectorKeyProtector(Protection(configuration));
        Guid? Server(string host, AgentPlatform platform) => hosts.TryGetValue(host, out var h)
            ? db.Agents.Local.FirstOrDefault(a => a.Platform == platform && db.Sensors.Local.Any(s => s.Id == a.SensorId && s.ParentId == h.Id))?.SensorId
            : null;
        var windows = Server("app-04", AgentPlatform.Windows);
        var linux = Server("web-01", AgentPlatform.Linux);

        ConnectorCallerEntity Caller(string name, string description, string from, params (string Cidr, string? Label)[] networks)
        {
            var key = ConnectorKeys.New();
            var caller = new ConnectorCallerEntity
            {
                Id = Guid.NewGuid(), Name = name, Description = description, Enabled = true,
                Networks = ConnectorNetworks.Write(networks.Select(n => new ConnectorNetwork(n.Cidr, n.Label))), KeyHash = ConnectorKeys.Hash(key),
                KeyProtected = protector.Protect(key), KeyHint = ConnectorKeys.Hint(key), KeyIssuedAt = now.AddDays(-20), CreatedAt = now.AddDays(-20),
                UpdatedAt = now.AddDays(-2), CreatedBy = "philipp", LastCallAt = now.AddMinutes(-3), LastCallFrom = from,
            };
            db.ConnectorCallers.Add(caller);
            return caller;
        }
        var shop = Caller("Shop back office", "The shop's back office, run by the web team; ask web@example.com.", "10.20.0.17", ("10.0.0.0/8", "LAN"));
        var lab = Caller("Lab", "The test lab's scripts: may call from anywhere, for now.", "192.0.2.44", ("0.0.0.0/0", null), ("::/0", null));

        ConnectorEntity Connector(string name, string slug, string purpose, ConnectorType type, ConnectorMode mode, string server, string scriptName, string text,
            string function, ParsedScript parsed, RunAsKind kind, string? account, bool review)
        {
            var connector = new ConnectorEntity
            {
                Id = Guid.NewGuid(), Name = name, Slug = slug, Purpose = purpose, Type = type, Enabled = true, Mode = mode, WaitSeconds = 60, ReturnOutput = review,
                MaxCallsPerHour = 30, MaxQueued = 5, ReviewEnabled = review,
                ReviewChecks = review ? ConnectorReview.WriteChecks([new("other work", "Does the note ask for anything other than a cache flush, such as a restart, a deletion, or access for someone?")]) : "[]",
                CreatedAt = now.AddDays(-14), CreatedBy = "philipp", UpdatedAt = now.AddDays(-2), LastCallAt = now.AddMinutes(-3),
            };
            connector.Script = new ConnectorScriptEntity
            {
                ConnectorId = connector.Id, Server = server, Name = scriptName, ScriptText = text, Hash = ScriptHashes.Of(text), ScriptRevision = 2,
                TakenFrom = $"library: {scriptName} v2", ParseState = ScriptParseState.Parsed, Parsed = JsonSerializer.Serialize(parsed, AgentJson.Options),
                ParsedAt = now.AddDays(-2), Function = function, RunAsKind = kind, Account = account, Shell = type == ConnectorType.LinuxScript ? "bash" : "powershell",
                TimeoutSeconds = 120,
            };
            var parameters = parsed.ParametersOf(function) ?? [];
            connector.Arguments = parameters.Select((p, i) => ConnectorArguments.New(connector.Id, p, i)).ToList();
            db.Connectors.Add(connector);
            return connector;
        }
        var flush = Connector("Shop: flush cache", "shop-flush-cache", "The shop's back office flushes a site's output cache after a product import.",
            ConnectorType.WindowsScript, ConnectorMode.Wait, "app-04 / Windows", "Shop-Tools.ps1", ShopTools, "Clear-ShopCache", SimulatedParser.Parse(ShopTools),
            RunAsKind.Stored, @"CORP\svc-shop", review: true);
        foreach (var a in flush.Arguments)
        {
            (a.Mode, a.Pattern, a.FixedValue, a.Required) = a.Name switch
            {
                "Site" => (ConnectorArgumentMode.Exposed, "shop-[a-z0-9]{2,20}", (string?)null, true),
                "Scope" => (ConnectorArgumentMode.Exposed, (string?)null, (string?)null, false),
                _ => (ConnectorArgumentMode.Fixed, (string?)null, "false", false),
            };
        }
        var backup = Connector("Backup: start job", "backup-start-job", "The scheduler starts a backup job on the backup server outside its plan.",
            ConnectorType.LinuxScript, ConnectorMode.Ticket, "web-01 / Linux", "backup-tools.sh", BackupTools, "start_job", BashScriptHeader.Parse(BackupTools),
            RunAsKind.Service, null, review: false);
        foreach (var (caller, connector) in new[] { (shop, flush), (shop, backup), (lab, backup) })
        {
            db.ConnectorGrants.Add(new ConnectorGrantEntity
            {
                CallerId = caller.Id, ConnectorId = connector.Id, Name = $"{caller.Name} → {connector.Name}", CreatedAt = now.AddDays(-14), CreatedBy = "philipp",
            });
        }

        var count = 0;
        AgentScriptRunEntity? Run(ConnectorEntity c, Guid? sensor, string serverName, DateTime at, ScriptRunState state, int? exit, string? output, string arguments)
        {
            if (sensor is null)
            {
                return null;
            }
            var run = new AgentScriptRunEntity
            {
                Id = Guid.NewGuid(), BatchId = Guid.NewGuid(), ScriptName = c.Script!.Name, VersionNumber = c.Script.ScriptRevision, Hash = c.Script.Hash, SensorId = sensor,
                ServerName = serverName, RunAsName = ConnectorTargets.RunAsName(c.Script.RunAsKind, c.Script.Account, c.Type == ConnectorType.LinuxScript),
                RunAsKind = c.Script.RunAsKind, Account = c.Script.Account, Arguments = arguments, RequestedBy = $"Shop back office via {c.Name}", StartedAt = at,
                FinishedAt = state is ScriptRunState.Running ? null : at.AddSeconds(3), State = state, ExitCode = exit, Output = output,
                DurationMs = state is ScriptRunState.Running ? null : 2851, TimeoutSeconds = 3600, ConnectorId = c.Id, Function = c.Script.Function,
            };
            db.AgentScriptRuns.Add(run);
            return run;
        }
        void Call(ConnectorEntity c, ConnectorCallerEntity caller, DateTime at, ConnectorCallState state, string arguments, string? refusedBy = null,
            string? message = null, string? waitingFor = null, ScriptRunState? run = null, int? exit = null, string? output = null, string? reference = null,
            bool dryRun = false, string? review = null)
        {
            var server = c.Type == ConnectorType.LinuxScript ? linux : windows;
            var row = run is { } s ? Run(c, server, c.Script!.Server, at.AddSeconds(1), s, exit, output, arguments) : null;
            var accepted = refusedBy is null or "review" or "agent" || waitingFor is not null || state is not ConnectorCallState.Refused;
            db.ConnectorCalls.Add(new ConnectorCallEntity
            {
                Id = Guid.NewGuid(), ConnectorId = c.Id, ConnectorName = c.Name, Slug = c.Slug, CallerId = caller.Id, CallerName = caller.Name, ReceivedAt = at,
                From = caller == lab ? "192.0.2.44" : "10.20.0.17", State = state, Accepted = accepted, DryRun = dryRun, Mode = c.Mode, RefusedBy = refusedBy,
                WaitingFor = waitingFor, ExpiresAt = at.AddSeconds(c.MaxQueueSeconds), StartedAt = row?.StartedAt, FinishedAt = ConnectorGateway.IsActive(state) ? null : at.AddSeconds(4),
                ServerSensorId = server, ServerName = c.Script!.Server, ScriptName = c.Script.Name, ScriptRevision = c.Script.ScriptRevision, Hash = c.Script.Hash,
                Function = c.Script.Function, Message = message, Arguments = arguments, Reference = reference, Review = review, RunId = row?.Id,
                CollectedAt = c.Mode == ConnectorMode.Ticket && !ConnectorGateway.IsActive(state) ? at.AddSeconds(30) : null,
            });
            count++;
        }
        var reviewed = new ConnectorReviewRecord(ConnectorReviewRecord.Reviewed, 0.5, new Dictionary<string, double> { ["injection"] = 0.01, ["purpose"] = 0.03, ["check1"] = 0.02 },
            new Dictionary<string, string> { ["injection"] = "injection", ["purpose"] = "purpose", ["check1"] = "other work" }, null, null, null).ToJson();
        var refusedReview = new ConnectorReviewRecord(ConnectorReviewRecord.Refused, 0.5, new Dictionary<string, double> { ["injection"] = 0.04, ["purpose"] = 0.71, ["check1"] = 0.88 },
            new Dictionary<string, string> { ["injection"] = "injection", ["purpose"] = "purpose", ["check1"] = "other work" }, "check1", "The review refused the call: other work.", null).ToJson();
        const string site = """{"Site":"shop-de","Scope":"Pages"}""";
        Call(flush, shop, now.AddMinutes(-3), ConnectorCallState.Done, site, run: ScriptRunState.Done, exit: 0, output: "Flushing shop-de (Pages)\nFlushed 1284 entries.\n",
            reference: "IMPORT-8812", review: reviewed);
        Call(flush, shop, now.AddHours(-5), ConnectorCallState.Done, """{"Site":"shop-ch","Scope":"All"}""", run: ScriptRunState.Done, exit: 0,
            output: "Flushing shop-ch (All)\nFlushed 3410 entries.\n", reference: "IMPORT-8807", review: reviewed);
        Call(flush, shop, now.AddHours(-6), ConnectorCallState.Refused, """{"Site":"shop-de"}""", "review", "The review refused the call: other work.", review: refusedReview,
            reference: "IMPORT-8806");
        Call(flush, shop, now.AddHours(-9), ConnectorCallState.Refused, "{}", "arguments_invalid",
            "1 argument is not allowed. Site: doesn't match the pattern shop-[a-z0-9]{2,20}");
        Call(flush, lab, now.AddHours(-26), ConnectorCallState.Refused, "{}", "unknown_connector", "No connector \"shop-flush-cache\" is granted to Lab.");
        Call(backup, shop, now.AddMinutes(-8), ConnectorCallState.Running, """{"job":"weekly"}""", run: ScriptRunState.Running, reference: "SCHED-311");
        Call(backup, lab, now.AddMinutes(-2), ConnectorCallState.Queued, """{"job":"nightly"}""", waitingFor: "turn", reference: "LAB-7");
        Call(backup, shop, now.AddHours(-2), ConnectorCallState.Failed, """{"job":"monthly"}""", run: ScriptRunState.Failed, exit: 1,
            output: "Job for backup-monthly.service failed: see journalctl -xeu backup-monthly.service\n", reference: "SCHED-309");
        Call(backup, shop, now.AddHours(-12), ConnectorCallState.Done, """{"job":"nightly"}""", run: ScriptRunState.Done, exit: 0, output: "started nightly\n",
            reference: "SCHED-301");
        Call(backup, lab, now.AddHours(-30), ConnectorCallState.Expired, """{"job":"weekly"}""", message: "Still queued after 600 s, waiting for the server's agent, which was offline.");
        Call(backup, lab, now.AddHours(-31), ConnectorCallState.Cancelled, """{"job":"weekly"}""", message: "Cancelled by philipp while queued.");
        Call(backup, lab, now.AddHours(-40), ConnectorCallState.Checked, """{"job":"weekly"}""", message: "The checks passed: the call would run on web-01 / Linux.",
            dryRun: true);
        Call(backup, shop, now.AddHours(-41), ConnectorCallState.Refused, "{}", "address_refused", "The key of Shop back office came from 203.0.113.9, outside its networks.");
        return count;
    }

    /// <summary>The app's key ring when the seed can write to it (the container), so the keys can be shown; otherwise one in memory.</summary>
    internal static IDataProtectionProvider Protection(IConfiguration configuration)
    {
        try
        {
            var services = new ServiceCollection();
            services.AddPnmtdDataProtection(configuration);
            var provider = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
            provider.CreateProtector("probe").Protect("probe");
            return provider;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        }
    }
}
