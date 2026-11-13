using System.Text.Json;
using PNMTD.Data;
using PNMTD.Models.Db;

namespace PNMTD.Windows;

/// <summary>
/// Changes (docs/windows-sensor-plan.md §4.9): a section with a new hash is compared with the stored one by its section's
/// differ, which writes readable lines, and 253 for the notable ones. The differs come with W3.
/// </summary>
public sealed class WindowsChanges
{
    public Task CompareAsync(PnmtdDbContext db, WindowsAgentEntity agent, string section, byte[] previous, JsonElement current, CancellationToken ct) =>
        Task.CompletedTask;
}
