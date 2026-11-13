using System.Net.NetworkInformation;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Service;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows.Join;

/// <summary>What the join ended with: the server as PNMT named it, and both keys' fingerprints to compare.</summary>
internal sealed record JoinResult(JoinGrant Grant, string AgentFingerprint, string PnmtdFingerprint, bool Connected);

/// <summary>
/// Joining (docs/windows-sensor-plan.md §3.3), for the window and the command line: check PNMT's URL, create the key, register,
/// write agent.json, install and start the service, and wait until it is connected.
/// </summary>
internal static class Joiner
{
    public static async Task<JoinResult> JoinAsync(string url, string token, string? name, IProgress<string> steps, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUrl) || baseUrl.Scheme is not ("https" or "http"))
        {
            throw new AgentJoinException("invalid_url", "PNMT's URL must start with https://.");
        }
        if (JoinTokens.Normalize(token) is not { } normalized)
        {
            throw new AgentJoinException("invalid_token", "The token has the form xxxx-xxxx.");
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"pnmt-sensor/{AgentInfo.Version}");

        steps.Report("Checking PNMT");
        var info = await AgentJoinClient.InfoAsync(http, baseUrl, ct);
        if (baseUrl.Scheme == "http" && !info.AllowHttp)
        {
            throw new AgentJoinException("https_required", "PNMT must be reached over https:// (Windows__AllowHttp is for development only).");
        }

        steps.Report("Creating the key");
        var previous = AgentFiles.ReadConfig();
        var keyName = AgentKey.NewName();
        using var key = AgentKey.Create(keyName);
        try
        {
            steps.Report("Registering");
            var os = WindowsVersion.Read();
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var request = new JoinRequest(normalized, AgentSigning.PublicKey(key), Environment.MachineName,
                string.IsNullOrEmpty(properties.DomainName) ? null : $"{properties.HostName}.{properties.DomainName}",
                string.IsNullOrEmpty(properties.DomainName) ? Environment.UserDomainName : properties.DomainName, os, AgentInfo.Version, AgentInfo.Architecture,
                WindowsVersion.MachineGuid(), string.IsNullOrWhiteSpace(name) ? null : name.Trim());
            var (response, grant) = await AgentJoinClient.JoinAsync(http, baseUrl, request, ct);

            AgentFiles.Secure();
            AgentFiles.WriteConfig(new AgentConfigFile(baseUrl.ToString(), grant.AgentId, grant.SensorId, grant.SensorName, grant.HostName, keyName,
                grant.AgentKeyFingerprint, response.SigningPublicKey, response.SigningKeyFingerprint, DateTimeOffset.UtcNow, grant.Config, previous?.LogLevel,
                info.AllowHttp));
            if (previous is not null && previous.KeyName != keyName)
            {
                AgentKey.Delete(previous.KeyName);
            }

            steps.Report("Installing the service");
            var executable = ServiceControl.InstallProgram();
            ServiceControl.Install(executable);
            if (ServiceControl.Status() == System.ServiceProcess.ServiceControllerStatus.Running)
            {
                ServiceControl.Restart();
            }
            else
            {
                ServiceControl.Start();
            }

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
            AgentKey.Delete(keyName);
            throw;
        }
    }
}
