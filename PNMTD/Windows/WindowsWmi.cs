using PNMTD.Lib.Agent;

namespace PNMTD.Windows;

/// <summary>A Windows server's WMI settings (docs/windows-sensor-plan.md §16), stored on its agent row as JSON: Windows' own after L1.</summary>
public static class WindowsWmi
{
    /// <summary>The stored settings, or none (also for a Linux agent, which has none).</summary>
    public static WmiConfig Settings(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return WmiConfig.Empty;
        }
        try
        {
            return AgentJson.Deserialize<WmiConfig>(json) ?? WmiConfig.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            return WmiConfig.Empty;
        }
    }
}
