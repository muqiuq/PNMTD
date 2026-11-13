using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Mikrotik;
using PNMTD.Models.Enums;

namespace PNMTD.Tests.Mikrotik;

/// <summary>
/// Update jobs (docs/mikrotik-plan.md §7, §14) against a simulated router that answers like RouterOS 7.25.1
/// did in the lab: the admin's group, backup save, SFTP, the channel, check and download, and the reboot.
/// The app's own job runner runs them.
/// </summary>
[TestClass]
public sealed class MikrotikJobTests
{
    internal const string AdminPassword = "marker-Zq7#pw-9f31";

    private static TestApp app = null!;
    private static AdminConnector routers = null!;
    private static HttpClient admin = null!;
    private static Guid hostId;
    private static int next = 60;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        routers = new AdminConnector();
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Mikrotik:AllowPasswordsOverHttp"] = "true",
            ["Mikrotik:Updates:PollSeconds"] = "1",
        }, services: s => s.Replace(ServiceDescriptor.Singleton<IRouterConnector>(routers)));
        admin = app.Client(Roles.Admin);
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"job-tests-{Guid.NewGuid():N}" })).ReadAsync<HostDetail>();
        hostId = host.Id;
        await app.Services.GetRequiredService<MikrotikKeyStore>().GetAsync(CancellationToken.None);
        await using var db = app.NewDb();
        var settings = await db.MikrotikSettings.SingleAsync();
        previousVersions = settings.LatestVersions;
        settings.LatestVersions = "{\"stable\":{\"version\":\"7.25.2\",\"releasedAt\":null}}";
        await db.SaveChangesAsync();
    }

    private static string? previousVersions;

    /// <summary>The catalog is the run's: the other classes find it as it was.</summary>
    [ClassCleanup]
    public static void Stop()
    {
        using (var db = app.NewDb())
        {
            db.MikrotikSettings.Single().LatestVersions = previousVersions;
            db.SaveChanges();
        }
        app.Dispose();
    }

    private static async Task<(Guid Id, SimulatedRouter Router)> RouterAsync()
    {
        var address = $"198.51.100.{next++}";
        var router = new SimulatedRouter();
        routers.Add(address, router);
        var sensor = await (await admin.PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = hostId, Type = SensorType.MIKROTIK, Name = $"router {address}", Interval = 300, GracePeriod = 600,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = address },
        })).ReadAsync<SensorDetail>();
        // The first login pins the host key: a password goes only to a known router.
        await app.Services.GetRequiredService<RouterPollService>().PollAsync(sensor.Id, wait: true, CancellationToken.None);
        return (sensor.Id, router);
    }

    private static async Task<MikrotikJobDetail> FinishedAsync(Guid jobId)
    {
        for (var i = 0; i < 300; i++)
        {
            var detail = await (await admin.GetAsync($"/api/v1/mikrotik/jobs/{jobId}")).ReadAsync<MikrotikJobDetail>();
            if (detail.Job.State is not (MikrotikJobState.Queued or MikrotikJobState.Running))
            {
                return detail;
            }
            await Task.Delay(100);
        }
        Assert.Fail("The job did not finish.");
        return null!;
    }

    private static Task<HttpResponseMessage> UpdateAsync(Guid sensorId, string password = AdminPassword, bool backup = true) =>
        admin.PostJsonAsync("/api/v1/mikrotik/jobs", new MikrotikJobRequest
        {
            SensorIds = [sensorId], Username = "admin", Password = password, BinaryBackup = backup,
        });

    [TestMethod]
    public async Task An_update_runs_every_step_and_writes_242()
    {
        var (id, router) = await RouterAsync();
        var created = await (await UpdateAsync(id)).ReadAsync<MikrotikJobsCreated>();
        Assert.AreEqual(0, created.Refused.Count, string.Join("; ", created.Refused.Select(r => r.Reason)));
        var job = await FinishedAsync(created.Jobs[0].Id);
        Assert.AreEqual(MikrotikJobState.Succeeded, job.Job.State, job.Job.Error);
        Assert.AreEqual("7.25.1", job.Job.FromVersion);
        Assert.AreEqual("7.25.2", job.Job.ToVersion);
        Assert.AreEqual("admin", job.Job.AdminUsername);
        CollectionAssert.IsSubsetOf(
            new[] { MikrotikJobStep.Check, MikrotikJobStep.Export, MikrotikJobStep.BinaryBackup, MikrotikJobStep.Download, MikrotikJobStep.Reboot,
                MikrotikJobStep.Wait, MikrotikJobStep.Verify, MikrotikJobStep.Done },
            job.Log.Select(l => l.Step).Distinct().ToArray());
        Assert.IsTrue(router.Commands.Any(c => c.StartsWith("/system backup save", StringComparison.Ordinal)));
        Assert.IsFalse(router.Files.Any(), "the backup file is removed from the router");
        Assert.AreEqual(1, router.Reboots);

        await using var db = app.NewDb();
        var backups = await db.MikrotikBackups.Where(b => b.SensorId == id).ToListAsync();
        Assert.IsTrue(backups.Any(b => b.Kind == MikrotikBackupKind.Binary && b.Encrypted && b.JobId == job.Job.Id));
        Assert.IsTrue(backups.Any(b => b.Kind == MikrotikBackupKind.Export));
        Assert.IsTrue(backups.Any(b => b.Kind == MikrotikBackupKind.Export && b.Trigger == MikrotikBackupTrigger.AfterUpdate),
            "a fresh export after the update (§7.3, step 9)");
        Assert.AreEqual(MikrotikJobOutcome.Ok, job.Log[^1].Outcome);
        var events = await db.Events.Where(e => e.SensorId == id).ToListAsync();
        Assert.IsTrue(events.Any(e => e.Code == PNMTStatusCodes.MIKROTIK_UPDATED && e.Message == "Updated 7.25.1 → 7.25.2"));

        // A binary backup is an Admin's, and the password is nowhere in the database.
        var user = app.Client(Roles.User);
        var binary = backups.Single(b => b.Kind == MikrotikBackupKind.Binary);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups/{binary.Id}/download")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/sensors/{id}/mikrotik/backups/{binary.Id}/download")).StatusCode);
        Assert.AreEqual(0, await CountMarkerAsync(app, AdminPassword), "the admin password must never be stored");
    }

    [TestMethod]
    public async Task Wrong_credentials_and_missing_policies_are_refused_before_a_job_exists()
    {
        var (id, router) = await RouterAsync();
        var wrong = await (await UpdateAsync(id, "not-the-password")).ReadAsync<MikrotikJobsCreated>();
        Assert.AreEqual(0, wrong.Jobs.Count);
        StringAssert.Contains(wrong.Refused.Single().Reason, "Wrong username or password");

        router.AdminPolicies = ["ssh", "read", "write", "reboot"];
        var weak = await (await UpdateAsync(id)).ReadAsync<MikrotikJobsCreated>();
        Assert.AreEqual("admin lacks the policies policy, test, sensitive, ftp.", weak.Refused.Single().Reason);
        var noBackup = await (await UpdateAsync(id, backup: false)).ReadAsync<MikrotikJobsCreated>();
        Assert.AreEqual("admin lacks the policies policy.", noBackup.Refused.Single().Reason);
        await using var db = app.NewDb();
        Assert.IsFalse(await db.MikrotikJobs.AnyAsync(j => j.SensorId == id));
    }

    [TestMethod]
    public async Task A_router_that_does_not_apply_the_package_fails_with_443()
    {
        var (id, router) = await RouterAsync();
        router.ApplyOnReboot = false;
        var created = await (await UpdateAsync(id, backup: false)).ReadAsync<MikrotikJobsCreated>();
        var job = await FinishedAsync(created.Jobs[0].Id);
        Assert.AreEqual(MikrotikJobState.Failed, job.Job.State);
        Assert.AreEqual(MikrotikJobStep.Verify, job.Job.Step);
        StringAssert.Contains(job.Job.Error, "the package was not applied");
        await using var db = app.NewDb();
        var failed = await db.Events.Where(e => e.SensorId == id && e.Code == PNMTStatusCodes.MIKROTIK_UPDATE_FAILED).SingleAsync();
        StringAssert.StartsWith(failed.Message, "Update to 7.25.2 failed at Verify");
    }

    [TestMethod]
    public async Task Passwords_only_over_HTTPS_and_only_Admins_update()
    {
        var (id, _) = await RouterAsync();
        using var strict = new TestApp();
        var refused = await strict.Client(Roles.Admin).PostJsonAsync("/api/v1/mikrotik/jobs", new MikrotikJobRequest { SensorIds = [id], Username = "admin", Password = "x" });
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        StringAssert.Contains(await refused.Content.ReadAsStringAsync(), "HTTPS");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.User).PostJsonAsync("/api/v1/mikrotik/jobs",
            new MikrotikJobRequest { SensorIds = [id], Username = "admin", Password = "x" })).StatusCode);
    }

    [TestMethod]
    public async Task An_up_to_date_router_is_refused()
    {
        var (id, router) = await RouterAsync();
        router.Version = "7.25.2";
        await app.Services.GetRequiredService<RouterPollService>().PollAsync(id, wait: true, CancellationToken.None);
        var current = await (await UpdateAsync(id)).ReadAsync<MikrotikJobsCreated>();
        StringAssert.Contains(current.Refused.Single().Reason, "Already on 7.25.2");
    }

    /// <summary>Every text column of every table, searched for <paramref name="marker"/> (§14: the password never persists).</summary>
    internal static async Task<int> CountMarkerAsync(TestApp app, string marker)
    {
        await using var db = app.NewDb();
        var columns = await db.Database.SqlQueryRaw<ColumnRow>("""
            SELECT table_name AS "Table", column_name AS "Column" FROM information_schema.columns
            WHERE table_schema = 'public' AND data_type IN ('text', 'character varying', 'jsonb')
            """).ToListAsync();
        var total = 0;
        foreach (var c in columns)
        {
            total += await db.Database.SqlQueryRaw<int>(
                $"SELECT COUNT(*)::int AS \"Value\" FROM \"{c.Table}\" WHERE \"{c.Column}\"::text LIKE '%' || {{0}} || '%'", marker).SingleAsync();
        }
        return total;
    }

    private sealed record ColumnRow(string Table, string Column);

    // --- the simulated router ------------------------------------------------------------------------------

    /// <summary>RouterOS as the job sees it: pnmt by key, admin by password, and the update's commands.</summary>
    internal sealed class SimulatedRouter
    {
        private readonly string poll = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mikrotik", "7.25.1", "poll.json"));
        private readonly string export = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mikrotik", "7.25.1", "export.rsc"));

        public string Version { get; set; } = "7.25.1";

        public string Uptime { get; set; } = "5d03:00:00";

        public string AdminPassword { get; set; } = MikrotikJobTests.AdminPassword;

        public string[] AdminPolicies { get; set; } = ["local", "telnet", "ssh", "ftp", "reboot", "read", "write", "policy", "test", "winbox", "password", "web", "sniff", "sensitive", "api"];

        public bool ApplyOnReboot { get; set; } = true;

        public string? Downloaded { get; set; }

        public int Reboots { get; set; }

        public List<string> Commands { get; } = [];

        public Dictionary<string, byte[]> Files { get; } = [];

        public RouterHostKey HostKey { get; } = new("ssh-ed25519", "SimulatedHostKeySimulatedHostKeySimulatedHo");

        /// <summary>For the command tests: its identity, configuration added by admin runs, and a log to print.</summary>
        public string Identity { get; set; } = "router";

        public List<string> Config { get; } = [];

        public int LogBytes { get; set; } = 100;

        /// <summary>For the key rotation: the owner (comment) of pnmt's key on the router, and the keys PNMTD offered last.</summary>
        public string? KeyOwner { get; set; }

        public int OfferedKeys { get; set; }

        /// <summary>The scripts run through <c>:parse</c> (§8.2), unwrapped.</summary>
        public List<string> Scripts { get; } = [];

        public async Task<RouterExecResult> ExecAsync(string command, bool asAdmin, CancellationToken ct)
        {
            const string wrapper = ":local pnmtScript [:parse ";
            if (!command.StartsWith(wrapper, StringComparison.Ordinal))
            {
                return Exec(command, asAdmin);
            }
            var script = Unquote(command[wrapper.Length..command.LastIndexOf("]; $pnmtScript", StringComparison.Ordinal)]);
            Scripts.Add(script);
            RouterExecResult Result(string output, int? status = 0) => new(output, "", status, false, false, TimeSpan.FromMilliseconds(5));
            switch (script)
            {
                case "/ip address print":
                    // As a terminal might: CR LF, and an escape sequence for colour.
                    return Result("\u001b[mFlags: D - DYNAMIC\r\n#   ADDRESS        INTERFACE\r\n0   10.0.2.15/24   ether1\r\n");
                case "/system identity print":
                    return Result($"  name: {Identity}\r\n");
                case "/log print":
                    return Result(string.Concat(Enumerable.Repeat("00:00:01 system,info router rebooted\r\n", LogBytes / 38 + 1)));
                case "/tool torch interface=ether1":
                    await Task.Delay(Timeout.Infinite, ct);
                    break;
            }
            if (script.StartsWith(":delay", StringComparison.Ordinal))
            {
                return new RouterExecResult("partial output\r\n", "", null, TimedOut: true, false, TimeSpan.FromSeconds(5));
            }
            if (script == "/flaky")
            {
                // M6 on 7.25.1: about one failed script in ten ends with exit status 0; the error line tells.
                return Result("bad command name flaky (line 1 column 2)\n", 0);
            }
            if (script.StartsWith(":error ", StringComparison.Ordinal))
            {
                return Result($"{script[8..^1]} (:error; line 1)\n", 1);
            }
            if (script.StartsWith("/ip address add ", StringComparison.Ordinal))
            {
                if (!asAdmin)
                {
                    return Result("not enough permissions (9)\n", 1);
                }
                Config.Add(script);
                return Result("");
            }
            return Result($"bad command name {script.Split(' ')[0].TrimStart('/')} (line 1 column 2)\n", 1);
        }

        /// <summary>RouterOsScript.Quote backwards.</summary>
        private static string Unquote(string quoted)
        {
            var bytes = new List<byte>();
            for (var i = 1; i < quoted.Length - 1; i++)
            {
                if (quoted[i] != '\\')
                {
                    bytes.Add((byte)quoted[i]);
                    continue;
                }
                var c = quoted[++i];
                switch (c)
                {
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case '"' or '\\' or '$' or '?': bytes.Add((byte)c); break;
                    default:
                        bytes.Add(Convert.ToByte(quoted.Substring(i, 2), 16));
                        i++;
                        break;
                }
            }
            return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        }

        public RouterExecResult Exec(string command, bool asAdmin)
        {
            Commands.Add(command);
            RouterExecResult Ok(string output = "") => new(output, "", 0, false, false, TimeSpan.Zero);
            RouterExecResult Denied() => new("not enough permissions (9)", "", 1, false, false, TimeSpan.Zero);
            // A newer RouterOS exports one more default, as upgrades often do; admin runs add their lines.
            if (command.StartsWith("/export", StringComparison.Ordinal))
            {
                return Ok((Version == "7.25.1" ? export : export + "/ip dns set max-concurrent-queries=200\n") + string.Concat(Config.Select(c => c + "\n")));
            }
            if (command.StartsWith("{ :local r", StringComparison.Ordinal))
            {
                var json = JsonNode.Parse(poll)!;
                json["version"] = "v" + Version;
                json["resource"]!["version"] = Version;
                json["resource"]!["uptime"] = Uptime;
                if (KeyOwner is not null)
                {
                    json["keys"] = new JsonArray(new JsonObject { ["info"] = KeyOwner, ["user"] = "pnmt", ["key-type"] = "ed25519" });
                }
                return Ok(json.ToJsonString());
            }
            if (command.StartsWith("{ :local g [/user/get", StringComparison.Ordinal))
            {
                return Ok($"[{{\"name\":\"full\",\"policy\":[{string.Join(",", AdminPolicies.Select(p => $"\"{p}\""))}]}}]");
            }
            if (!asAdmin) return Denied();
            if (command.StartsWith("/system backup save name=", StringComparison.Ordinal))
            {
                var name = command.Split('"')[1];
                Files[$"{name}.backup"] = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
                return Ok("Configuration backup saved");
            }
            if (command.StartsWith("/file remove", StringComparison.Ordinal))
            {
                Files.Clear();
                return Ok();
            }
            if (command.StartsWith("/system package update set channel=", StringComparison.Ordinal)) return Ok();
            if (command.StartsWith("/system package update check-for-updates", StringComparison.Ordinal)) return Ok("status: New version is available");
            if (command.Contains("get latest-version", StringComparison.Ordinal)) return Ok("7.25.2");
            if (command == "/system package update download")
            {
                Downloaded = "7.25.2";
                return Ok("status: Downloaded, please reboot router to upgrade it");
            }
            if (command.Contains("get status", StringComparison.Ordinal)) return Ok(Downloaded is null ? "New version is available" : "Downloaded, please reboot router to upgrade it");
            if (command == "/system reboot")
            {
                Reboots++;
                if (ApplyOnReboot && Downloaded is not null) Version = Downloaded;
                Uptime = "00:00:05";
                return Ok();
            }
            return new RouterExecResult($"bad command name ({command})", "", 1, false, false, TimeSpan.Zero);
        }
    }

    internal sealed class AdminConnector : IRouterConnector
    {
        private readonly Dictionary<string, SimulatedRouter> byHost = new();

        public void Add(string host, SimulatedRouter router) => byHost[host] = router;

        public Task<IRouterSession> ConnectAsync(RouterTarget target, CancellationToken ct)
        {
            if (!byHost.TryGetValue(target.Host, out var router))
            {
                throw new RouterSessionException(RouterFailureKind.Unreachable, "SSH: connection timed out");
            }
            if (target.PinnedHostKey is { } pinned && pinned != router.HostKey)
            {
                throw new RouterSessionException(RouterFailureKind.HostKeyChanged, "Host key changed", router.HostKey);
            }
            var admin = target.Credentials.Password is not null;
            if (!admin)
            {
                router.OfferedKeys = target.Credentials.PrivateKeysPem.Count;
            }
            if (admin && (target.Username != "admin" || target.Credentials.Password != router.AdminPassword))
            {
                throw new RouterSessionException(RouterFailureKind.AuthenticationFailed, "SSH: login refused");
            }
            return Task.FromResult<IRouterSession>(new Session(router, admin));
        }

        private sealed class Session(SimulatedRouter router, bool admin) : IRouterSession
        {
            public RouterHostKey HostKey => router.HostKey;

            public Task<RouterExecResult> ExecAsync(string command, TimeSpan timeout, int maxOutputBytes, CancellationToken ct) =>
                router.ExecAsync(command, admin, ct);

            public Task<byte[]> DownloadAsync(string path, long maxBytes, CancellationToken ct) =>
                router.Files.TryGetValue(path, out var file) ? Task.FromResult(file) : throw new RouterSessionException(RouterFailureKind.Protocol, "SFTP: no such file");

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
