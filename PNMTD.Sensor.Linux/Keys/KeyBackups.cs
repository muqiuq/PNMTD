using System.Globalization;
using System.Text.Json;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Keys;

/// <summary>What a backup holds besides the file: where it came from, whose and with which mode, and what was removed.</summary>
internal sealed record KeyBackup(
    string Name,
    string Account,
    string Path,
    uint Uid,
    uint Gid,
    string Mode,
    string Fingerprint,
    Guid? RemovalId,
    DateTimeOffset At,
    string Sha256,
    int LinesRemoved);

/// <summary>
/// The key files as they were before a removal (docs/linux-sensor-plan.md §6.5 step 5): <c>state/key-backups/&lt;account&gt;/&lt;UTC time&gt;-&lt;file
/// name&gt;</c>, root's, 0600, with a <c>.json</c> next to each that says where it belongs. Kept 30 days, and at least the last 10 per file.
/// <c>pnmt-agent keys restore</c> puts one back (§6.5: restoring is local only).
/// </summary>
internal static class KeyBackups
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);
    public const int KeepAtLeast = 10;

    public static string Folder => System.IO.Path.Combine(AgentInfo.StateFolder, "key-backups");

    private static readonly JsonSerializerOptions Indented = new(AgentJson.Options) { WriteIndented = true };

    /// <summary>Saves the file as it was; answers the backup's name (<c>deploy/20261114T103000Z-authorized_keys</c>).</summary>
    public static string Save(string account, string path, byte[] content, FileFacts facts, string fingerprint, Guid? removalId, int linesRemoved)
    {
        var safeAccount = Safe(account);
        var folder = System.IO.Path.Combine(Folder, safeAccount);
        LinuxPlatform.SecureFolder(Folder);
        LinuxPlatform.SecureFolder(folder);
        var at = DateTimeOffset.UtcNow;
        var stem = $"{at:yyyyMMdd'T'HHmmss'Z'}-{Safe(System.IO.Path.GetFileName(path))}";
        var file = stem;
        for (var i = 2; File.Exists(System.IO.Path.Combine(folder, file)); i++)
        {
            file = $"{stem}-{i}";
        }
        var target = System.IO.Path.Combine(folder, file);
        Write(target, content);
        var name = $"{safeAccount}/{file}";
        var meta = new KeyBackup(name, account, path, facts.Uid, facts.Gid, facts.Octal, fingerprint, removalId, at, KeyRewrite.Sha(content), linesRemoved);
        Write(target + ".json", System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta, Indented)));
        Prune(path);
        return name;
    }

    private static void Write(string path, byte[] content)
    {
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Every backup, newest first.</summary>
    public static List<KeyBackup> List()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }
        var result = new List<KeyBackup>();
        foreach (var meta in Directory.GetFiles(Folder, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                if (JsonSerializer.Deserialize<KeyBackup>(File.ReadAllText(meta), AgentJson.Options) is { } backup && File.Exists(meta[..^5]))
                {
                    result.Add(backup);
                }
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
            }
        }
        return result.OrderByDescending(b => b.At).ToList();
    }

    /// <summary>A backup by its name (<c>deploy/2026…-authorized_keys</c>), its file name alone, or its path.</summary>
    public static KeyBackup? Find(string name)
    {
        var all = List();
        var trimmed = name.StartsWith(Folder + "/", StringComparison.Ordinal) ? name[(Folder.Length + 1)..] : name;
        trimmed = trimmed.EndsWith(".json", StringComparison.Ordinal) ? trimmed[..^5] : trimmed;
        return all.FirstOrDefault(b => b.Name == trimmed) ?? (all.Count(b => b.Name.EndsWith("/" + trimmed, StringComparison.Ordinal)) == 1
            ? all.First(b => b.Name.EndsWith("/" + trimmed, StringComparison.Ordinal))
            : null);
    }

    public static byte[] Content(KeyBackup backup) => File.ReadAllBytes(System.IO.Path.Combine(Folder, backup.Name));

    /// <summary>§6.5 step 5: per original file, what is older than 30 days goes, as long as the 10 newest stay.</summary>
    public static void Prune(string path)
    {
        var mine = List().Where(b => b.Path == path).OrderByDescending(b => b.At).ToList();
        foreach (var old in mine.Skip(KeepAtLeast).Where(b => DateTimeOffset.UtcNow - b.At > KeepFor))
        {
            var file = System.IO.Path.Combine(Folder, old.Name);
            TryDelete(file);
            TryDelete(file + ".json");
        }
    }

    /// <summary>A backup that isn't needed after all (the rewrite didn't happen).</summary>
    public static void Delete(string name)
    {
        var file = System.IO.Path.Combine(Folder, name);
        TryDelete(file);
        TryDelete(file + ".json");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A name for the backup folder: the account's or file's own, without slashes or dots at its start.</summary>
    private static string Safe(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '@' ? c : '_').ToArray();
        var text = new string(chars).TrimStart('.');
        return text.Length == 0 ? "_" : text;
    }

    public static uint ParseMode(string octal) => Convert.ToUInt32(octal, 8);

    public static string Describe(KeyBackup b) =>
        string.Create(CultureInfo.InvariantCulture, $"{b.Name}  {b.At.ToLocalTime():yyyy-MM-dd HH:mm}  {b.Path} ({b.Account}): {b.Fingerprint} removed, {b.LinesRemoved} line{(b.LinesRemoved == 1 ? "" : "s")}");
}
