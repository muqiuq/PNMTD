using System.Net.WebSockets;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;

namespace PNMTD.Cli;

/// <summary>`pnmtd windows …` (docs/windows-sensor-plan.md §8.4, §11): join tokens, and a simulated agent.</summary>
internal static class WindowsCommand
{
    public const string Usage = """
          windows tokens [--all]                the pending join tokens (--all: used, expired and revoked too)
          windows tokens revoke <prefix|id>     revokes a pending token
          windows simulate --url <PNMT URL> [--token <xxxx-xxxx>] [--name <name>] [--hostname <name>]
                           [--state <file>] [--interval <seconds>] [--count <n>]
                                                a fake Windows agent: joins with the token (or uses --state,
                                                default pnmt-sensor-sim.json), connects, sends heartbeats,
                                                sections and logins, answers collect, diagnostics and send-log
        """;

    public static int Run(string[] args, Func<PnmtdDbContext> database) => args switch
    {
        ["tokens"] => Tokens(database, all: false),
        ["tokens", "--all"] => Tokens(database, all: true),
        ["tokens", "revoke", var id] => Revoke(database, id),
        ["simulate", .. var rest] => Simulate(new CommandLine.Options(rest)),
        _ => throw new CommandLineException($"Unknown windows command: {string.Join(' ', args)}"),
    };

    private static int Tokens(Func<PnmtdDbContext> database, bool all)
    {
        using var db = database();
        var now = Clock.UtcNow;
        var tokens = db.WindowsJoinTokens.AsNoTracking()
            .Where(t => all || (t.UsedAt == null && t.RevokedAt == null && t.ExpiresAt >= now))
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, t.Prefix, Host = db.Hosts.Where(h => h.Id == t.HostId).Select(h => h.Name).FirstOrDefault(), t.Name, t.ExpiresAt, t.UsedAt, t.RevokedAt, t.CreatedByName })
            .ToList();
        if (tokens.Count == 0)
        {
            Console.WriteLine(all ? "No join tokens." : "No pending join tokens.");
            return 0;
        }
        foreach (var t in tokens)
        {
            var state = t.UsedAt is not null ? "used" : t.RevokedAt is not null ? "revoked" : t.ExpiresAt < now ? "expired" : "pending";
            Console.WriteLine($"{t.Prefix}…  {state,-8} host {t.Host}{(t.Name is null ? "" : $", name {t.Name}")}, until {t.ExpiresAt:yyyy-MM-dd HH:mm} UTC, by {t.CreatedByName ?? "?"}  ({t.Id})");
        }
        return 0;
    }

    private static int Revoke(Func<PnmtdDbContext> database, string idOrPrefix)
    {
        using var db = database();
        var now = Clock.UtcNow;
        var pending = db.WindowsJoinTokens.Where(t => t.UsedAt == null && t.RevokedAt == null).ToList();
        var matches = Guid.TryParse(idOrPrefix, out var id)
            ? pending.Where(t => t.Id == id).ToList()
            : pending.Where(t => t.Prefix == idOrPrefix.Trim().TrimEnd('…', '.').ToLowerInvariant()).ToList();
        if (matches.Count != 1)
        {
            Console.Error.WriteLine(matches.Count == 0 ? "No pending token matches." : "Several pending tokens have this prefix: use the id.");
            return 1;
        }
        matches[0].RevokedAt = now;
        Windows.WindowsAgentLog.Audit(db, Auth.AuthEvents.WindowsJoinTokenRevoked, $"cli: token {matches[0].Prefix}… revoked", null);
        db.SaveChanges();
        Console.WriteLine($"Revoked {matches[0].Prefix}….");
        return 0;
    }

    private static int Simulate(CommandLine.Options o)
    {
        var url = new Uri(o.Required("--url").TrimEnd('/') + "/");
        var token = o.Optional("--token");
        var name = o.Optional("--name");
        var hostname = o.Optional("--hostname") ?? "SIM-SRV01";
        var statePath = o.Optional("--state") ?? "pnmt-sensor-sim.json";
        var interval = o.Optional("--interval") is { } i ? TimeSpan.FromSeconds(int.Parse(i, System.Globalization.CultureInfo.InvariantCulture)) : (TimeSpan?)null;
        var count = o.Optional("--count") is { } c ? int.Parse(c, System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
        o.EnsureAllUsed();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        var simulator = new WindowsSimulator(url, http, async ct =>
        {
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(AgentProtocol.Subprotocol);
            socket.Options.KeepAliveInterval = AgentProtocol.KeepAlive;
            await socket.ConnectAsync(WindowsSimulator.WebSocketUrl(url), ct);
            return socket;
        }, Console.Out);

        SimulatedAgentState state;
        if (token is not null)
        {
            state = simulator.JoinAsync(token, name, hostname, stop.Token).GetAwaiter().GetResult();
            File.WriteAllText(statePath, WindowsSimulator.Save(state));
            Console.WriteLine($"State saved to {statePath}.");
        }
        else if (File.Exists(statePath))
        {
            state = WindowsSimulator.Load(File.ReadAllText(statePath));
        }
        else
        {
            throw new CommandLineException($"--token <xxxx-xxxx> to join, or --state <file> of an earlier join ({statePath} does not exist).");
        }
        try
        {
            var end = simulator.RunAsync(state, stop.Token, interval, count).GetAwaiter().GetResult();
            return end.Revoked ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }
}
