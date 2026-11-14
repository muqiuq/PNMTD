using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.State;

namespace PNMTD.Sensor.Core.Join;

/// <summary>What the join ended with: the server as PNMT named it, and both keys' fingerprints to compare.</summary>
public sealed record JoinResult(JoinGrant Grant, string AgentFingerprint, string PnmtdFingerprint, bool Connected);

/// <summary>
/// Joining (docs/windows-sensor-plan.md §3.3; docs/linux-sensor-plan.md §4.4 steps 4–10), for the window and the command lines:
/// check PNMT's URL, create the key, register, write agent.json, install and start the service through the platform, and wait until
/// it is connected. Plain http:// needs a confirmation (<paramref name="confirmHttp"/> gets the URL and PNMT's key as the URL
/// answers), and the join must then answer that same key. The steps reported are the window's: "Checking PNMT", "Creating the key",
/// "Registering", "Installing the service", "Waiting for the connection", then "Connected" or not.
/// </summary>
public static class Joiner
{
    public static async Task<JoinResult> JoinAsync(IAgentPlatform platform, string url, string token, string? name, IProgress<string> steps,
        Func<Uri, string, Task<bool>> confirmHttp, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUrl) || baseUrl.Scheme is not ("https" or "http"))
        {
            throw new AgentJoinException("invalid_url", "PNMT's URL starts with https:// (or http://, after a confirmation).");
        }
        if (JoinTokens.Normalize(token) is not { } normalized)
        {
            throw new AgentJoinException("invalid_token", "The token has the form xxxx-xxxx.");
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{platform.UserAgent}/{platform.Version}");

        steps.Report("Checking PNMT");
        var info = await AgentJoinClient.InfoAsync(http, baseUrl, ct);
        var plainHttp = baseUrl.Scheme == "http";
        if (plainHttp && !await confirmHttp(baseUrl, info.SigningKeyFingerprint))
        {
            throw new AgentJoinException("http_declined", "Not joined: plain http:// was not confirmed.");
        }

        steps.Report("Creating the key");
        var previous = AgentFiles.ReadConfig();
        var keys = platform.Keys;
        var keyName = keys.NewName();
        using var key = keys.Create(keyName);
        try
        {
            steps.Report("Registering");
            var os = platform.ReadOs();
            var names = platform.Names();
            var request = new JoinRequest(normalized, AgentSigning.PublicKey(key), names.Hostname, names.Fqdn, names.Domain, os, platform.Version,
                platform.Architecture, platform.MachineId(), string.IsNullOrWhiteSpace(name) ? null : name.Trim(), platform.Platform);
            var (response, grant) = await AgentJoinClient.JoinAsync(http, baseUrl, request, ct);
            if (response.SigningKeyFingerprint != info.SigningKeyFingerprint)
            {
                // Over plain HTTP, the key confirmed is the one pinned; with HTTPS, a change between the two requests is odd enough too.
                throw new AgentJoinException("key_changed", $"PNMT answered with another key at the join ({response.SigningKeyFingerprint}) than at the check: refused.");
            }

            platform.SecureFolders();
            AgentFiles.WriteConfig(new AgentConfigFile(baseUrl.ToString(), grant.AgentId, grant.SensorId, grant.SensorName, grant.HostName, keyName,
                grant.AgentKeyFingerprint, response.SigningPublicKey, response.SigningKeyFingerprint, DateTimeOffset.UtcNow, grant.Config, previous?.LogLevel,
                plainHttp));
            if (previous is not null && previous.KeyName != keyName)
            {
                keys.Delete(previous.KeyName);
            }

            steps.Report("Installing the service");
            platform.Service.InstallAndStart();

            steps.Report("Waiting for the connection");
            var connected = false;
            var until = DateTimeOffset.UtcNow.AddSeconds(60);
            while (DateTimeOffset.UtcNow < until && !ct.IsCancellationRequested)
            {
                if (AgentFiles.ReadStatus() is { Connected: true } status && status.ConnectedSince >= DateTimeOffset.UtcNow.AddMinutes(-2))
                {
                    connected = true;
                    break;
                }
                await Task.Delay(1000, ct);
            }
            steps.Report(connected ? "Connected" : "Not connected yet: see the log");
            return new JoinResult(grant, grant.AgentKeyFingerprint, response.SigningKeyFingerprint, connected);
        }
        catch
        {
            keys.Delete(keyName);
            throw;
        }
    }
}
