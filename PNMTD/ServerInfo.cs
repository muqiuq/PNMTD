using System.Reflection;

namespace PNMTD;

/// <summary>What this instance is: the Identity setting (to tell instances apart) and the version.</summary>
public static class ServerInfo
{
    public static string Identity { get; set; } = "no identity set";

    public static string Version { get; } =
        typeof(ServerInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ServerInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
