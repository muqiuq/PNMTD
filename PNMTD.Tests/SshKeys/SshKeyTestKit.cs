using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PNMTD.Lib.Agent;
using PNMTD.Tests.Agents;

// The folder is PNMTD.Tests/SshKeys/; a namespace PNMTD.Tests.SshKeys would hide PNMTD.Lib.Agent.SshKeys in PNMTD.Tests.*.
namespace PNMTD.Tests.KeyManager;

/// <summary>A public key for the tests: its type, Base64, fingerprint, bits and comment, and its line as a .pub file has it.</summary>
internal sealed record TestKey(string Type, string Key, string Fingerprint, int? Bits, string? Comment)
{
    public string Line => Comment is null ? $"{Type} {Key}" : $"{Type} {Key} {Comment}";

    public static TestKey From(string line)
    {
        var key = SshKeys.ParseLine(line).Key ?? throw new ArgumentException("not a key", nameof(line));
        return new TestKey(key.Type, key.Key, key.Fingerprint, key.Bits, key.Comment);
    }
}

/// <summary>Keys, key files and ssh-keys sections for the key manager's tests (docs/linux-sensor-plan.md §6, §14).</summary>
internal static class SshKeyTestKit
{
    /// <summary>A fresh Ed25519 key of its own: tests share one database, so each registers keys nobody else has.</summary>
    public static TestKey NewKey(string comment)
    {
        static byte[] SshString(byte[] value) => [(byte)(value.Length >> 24), (byte)(value.Length >> 16), (byte)(value.Length >> 8), (byte)value.Length, .. value];
        var blob = SshString(Encoding.ASCII.GetBytes("ssh-ed25519")).Concat(SshString(RandomNumberGenerator.GetBytes(32))).ToArray();
        return new TestKey("ssh-ed25519", Convert.ToBase64String(blob), SshKeys.Fingerprint(blob), 256, comment);
    }

    public static AuthorizedKeysFile File(string account, string path, IEnumerable<TestKey> keys, string mode = "0600", bool removable = true,
        string? notRemovable = null, bool clusterWide = false) =>
        new(account, path, null, true, account, account, mode, 120, DateTimeOffset.UtcNow.AddDays(-1), "0f0e", clusterWide, [],
            keys.Select((k, i) => new AuthorizedKey(i + 1, k.Type, k.Bits, k.Fingerprint, k.Comment, [], k.Key, [], false)).ToList(), [], removable, notRemovable);

    public static AuthorizedKeysFile Root(params TestKey[] keys) => File("root", "/root/.ssh/authorized_keys", keys);

    public static AuthorizedKeysFile Deploy(params TestKey[] keys) => File("deploy", "/home/deploy/.ssh/authorized_keys", keys);

    public static SshKeysSection Section(params AuthorizedKeysFile[] files) =>
        new(new SshdKeySettings([".ssh/authorized_keys"], null, null, null, null, true, "prohibit-password", true, false, true), files, [], files.Sum(f => f.Keys.Count),
            false, []);

    /// <summary>The rights of a config.yaml that allows removing keys for every account (or those named).</summary>
    public static AgentRightsSummary RemovalRights(params string[] users) => new("config.yaml", null, true, users, 0, [], false, [], 0, 60, false, false);

    public static IReadOnlyList<string> LinuxCapabilities(bool keyRemoval) =>
    [
        AgentCapabilities.Collect, AgentCapabilities.Access, AgentCapabilities.Keys, AgentCapabilities.Parse,
        .. keyRemoval ? [AgentCapabilities.KeysRemove] : Array.Empty<string>(),
    ];

    /// <summary>Sends an ssh-keys section and waits until the key manager holds exactly its keys for this server.</summary>
    public static async Task SendKeysAsync(TestApp app, AgentSession session, Guid sensorId, SshKeysSection section)
    {
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.SshKeys, section), CancellationToken.None);
        var expected = section.Files.SelectMany(f => f.Keys.Select(k => $"{f.Account}|{f.Path}|{k.Fingerprint}")).Distinct().Order(StringComparer.Ordinal).ToList();
        await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            var rows = await db.SshKeyAuthorizations.AsNoTracking().Where(a => a.SensorId == sensorId).Select(a => a.Account + "|" + a.Path + "|" + a.Fingerprint).ToListAsync();
            return rows.Order(StringComparer.Ordinal).SequenceEqual(expected);
        });
    }
}
