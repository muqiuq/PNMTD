using PNMTD.Lib.Agent;

namespace PNMTD.Tests.Linux;

/// <summary>
/// The shared authorized_keys parser (docs/linux-sensor-plan.md §3.4, §14): keys with options and odd quoting, fingerprints equal
/// to <c>ssh-keygen -l</c>'s for keys it generated, bits, private keys recognised, notes, and a key's lines removed.
/// </summary>
[TestClass]
public sealed class SshKeysTests
{
    // Generated with ssh-keygen for these tests only; the fingerprints are ssh-keygen -l's.
    private const string Ed25519 = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAILq6kAtJ1KulJF1n9P40mDL2uqwTZSntIJjTMZUCoNx9 test-ed25519@pnmt";
    private const string Ed25519Fingerprint = "SHA256:bwW9qmkf4O+xf0OXE6Io4ilyh5Ox/o9pacDwKjfqvWs";
    private const string Rsa2048 = "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQC+XcEkXWjUcGPEaXCWEQ80DfNjvc7kEZ4zRcgrJh3Dt5e7xZ2VIx0c9PJL7dVb3KR2hDnqc1GyNGmNIslVaoStyxwqXe4txkifiaZrlZjtgub6xsNSsm5ApR0ei7kpGP8P6G9a+v3/LCxlTuY8T5lEnzEWwXNRx620Yo7QPUmBVA+gUfmYCKqHmEcO05a9U0aarm1omE/fzSgx9DyAD45QTrfTm+LfzeVBsWHDqeastJ9wlp1oL4m/GKtWL7MArPwPvmurmuxJnTYM8ZIXuYHMHGUZOzFcaiayYgU2I3NszMANETCMlOi4vtNeuc27hGBeHZ7iNsenAidW585zUKx7 rsa2048@pnmt";
    private const string Rsa2048Fingerprint = "SHA256:hnQLPE+A1RfwfcdBGS/gdJCSYGAWx1Jf/vQo/YBAgg0";
    private const string Rsa3072 = "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQC+Qv8+fD379CcQuFj5JAm6N0F+mYhss+Dw77343um3TN7t4smYqcxBQXvkRaXRaOMmUoeiOBxK5Q7StmUhO5Kh3YcvF0dLY+O8MGrFbAVP0XPJVz6djeUYNjSuLsF3njNaHIilipBSd7TJi5Fvs6mRvDb/heGBv8spFAlRPYmuJYo+cSSnAdbQZReZ0KL0624lllKtFLkzffQimIvYkUBKQFe1gzx3Iubj4keiHfzTXpE6LSWVHpWUf7kUXkEVfSCbwPOQ9UZAbIVPwuFrNETkn4Y6HcmIzNz/eok/r/Wa6fs73Vh1CT85mIV7tisHmJ+3J6ZbuiS5sUJtawQjgUvv2qpFYhhOrud/2jkGOMsQg2ughW3r6yMATjiNdFs5r7KptRhIQTmbbobE6WNxTq4fvP0YYgA6Syqqetdg8AuW1/pU0bLlgPBR/EbG4ncQH7ABxi6RQtauqZGHvx0JOfGR3XOervcrki/DySGdu1nmOKejbQvBFo97QXhl03L+vEc= rsa3072@pnmt";
    private const string Rsa3072Fingerprint = "SHA256:BZczXLiyzQkhnbldLJ0nFM7FRtkc+TSxDVW4dQDoOiw";
    private const string Ecdsa384 = "ecdsa-sha2-nistp384 AAAAE2VjZHNhLXNoYTItbmlzdHAzODQAAAAIbmlzdHAzODQAAABhBF/AosXWRo0Uum4PHLJH9x3pa2aTQwUftE4Sko2DxKMldxo9kjp92pqdpZVzIrvQLyUNl2AabOFaVPZHtHpzbyDVRbWiXOMphJ6Ok6q6e0RmgYwjseCrWgQ6IN7+BE6o4w== ecdsa384@pnmt";
    private const string Ecdsa384Fingerprint = "SHA256:gjy1yUZbUvURIs8uZD3WlQEPDEjqbj8UNpjBKmrihLc";
    private const string Rsa1024 = "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAAAgQCrNMD/M/cddTMVyrkuAcOUCJI+mQlr2bJ0pXqyCcMqAGOjswqMzddGfzwm08H/LDDuYEGQNfV+OG3Qf8zpr73iqmSqKP6aeOKjw+oGgcWfHKVYw2HLeTcHKR46m6wBhoS2dkPbb4bknYKUsLpRIGarVuOrpVnKmlqNbiynm3zYjQ==";
    private const string Rsa1024Fingerprint = "SHA256:s5KXPthdXJ7Dq+KU70XuqftKMr//YfHPBRSso2ZWgts";

    [TestMethod]
    [DataRow(Ed25519, "ssh-ed25519", 256, Ed25519Fingerprint, "test-ed25519@pnmt")]
    [DataRow(Rsa2048, "ssh-rsa", 2048, Rsa2048Fingerprint, "rsa2048@pnmt")]
    [DataRow(Rsa3072, "ssh-rsa", 3072, Rsa3072Fingerprint, "rsa3072@pnmt")]
    [DataRow(Ecdsa384, "ecdsa-sha2-nistp384", 384, Ecdsa384Fingerprint, "ecdsa384@pnmt")]
    [DataRow(Rsa1024, "ssh-rsa", 1024, Rsa1024Fingerprint, null)]
    public void Keys_have_ssh_keygens_fingerprint_and_size(string line, string type, int bits, string fingerprint, string? comment)
    {
        var (key, problem) = SshKeys.ParseLine(line);

        Assert.AreEqual(SshKeyLineProblem.None, problem);
        Assert.IsNotNull(key);
        Assert.AreEqual(type, key.Type);
        Assert.AreEqual(bits, key.Bits);
        Assert.AreEqual(fingerprint, key.Fingerprint);
        Assert.AreEqual(comment, key.Comment);
        Assert.AreEqual(0, key.Options.Count);
    }

    [TestMethod]
    public void Options_with_quotes_commas_and_spaces_are_read()
    {
        var line = "from=\"10.0.0.0/8,192.168.1.5\",command=\"/usr/bin/rsync --server -e.LsfxC . \\\"/srv/backup\\\"\",no-pty,restrict "
            + Ed25519;

        var (key, problem) = SshKeys.ParseLine(line);

        Assert.AreEqual(SshKeyLineProblem.None, problem);
        Assert.AreEqual(Ed25519Fingerprint, key!.Fingerprint);
        Assert.AreEqual(4, key.Options.Count);
        Assert.AreEqual("10.0.0.0/8,192.168.1.5", SshKeys.Option(key.Options, "from"));
        Assert.AreEqual("/usr/bin/rsync --server -e.LsfxC . \"/srv/backup\"", SshKeys.Option(key.Options, "command"));
        Assert.IsTrue(SshKeys.HasOption(key.Options, "restrict"));
        Assert.IsTrue(SshKeys.HasOption(key.Options, "no-pty"));
    }

    [TestMethod]
    [DataRow("", SshKeyLineProblem.Blank)]
    [DataRow("   # a comment", SshKeyLineProblem.Blank)]
    [DataRow("ssh-ed25519 not-base64!", SshKeyLineProblem.Unreadable)]
    [DataRow("ssh-rsa AAAAC3NzaC1lZDI1NTE5AAAAILq6kAtJ1KulJF1n9P40mDL2uqwTZSntIJjTMZUCoNx9", SshKeyLineProblem.Unreadable)]
    [DataRow("root:x:0:0:root:/root:/bin/bash", SshKeyLineProblem.Unreadable)]
    [DataRow("-----BEGIN OPENSSH PRIVATE KEY-----", SshKeyLineProblem.PrivateKey)]
    [DataRow("from=\"unterminated ssh-ed25519 AAAA", SshKeyLineProblem.Unreadable)]
    public void Lines_that_are_not_keys_say_why(string line, SshKeyLineProblem expected)
    {
        var (key, problem) = SshKeys.ParseLine(line);

        Assert.IsNull(key);
        Assert.AreEqual(expected, problem);
    }

    [TestMethod]
    public void A_pasted_private_key_is_refused_as_a_whole()
    {
        var pasted = $"{Ed25519}\n-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----\n";

        var keys = SshKeys.ParseMany(pasted);

        Assert.AreEqual(1, keys.Count);
        Assert.AreEqual(SshKeyLineProblem.PrivateKey, keys[0].Problem);
        Assert.IsTrue(SshKeys.ContainsPrivateKey("PuTTY-User-Key-File-3: ssh-ed25519"));
    }

    [TestMethod]
    public void Several_pasted_keys_keep_their_line_numbers()
    {
        var keys = SshKeys.ParseMany($"{Ed25519}\n\n# old\n{Rsa2048}\nnonsense\n");

        Assert.AreEqual(3, keys.Count);
        Assert.AreEqual((1, Ed25519Fingerprint), (keys[0].Line, keys[0].Key!.Fingerprint));
        Assert.AreEqual((4, Rsa2048Fingerprint), (keys[1].Line, keys[1].Key!.Fingerprint));
        Assert.AreEqual((5, SshKeyLineProblem.Unreadable), (keys[2].Line, keys[2].Problem));
    }

    [TestMethod]
    public void Notes_name_weak_keys_roots_keys_without_from_and_passed_expiries()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var weak = SshKeys.ParseLine(Rsa1024).Key!;
        var expired = SshKeys.ParseLine("expiry-time=\"20250101\" " + Ed25519).Key!;
        var limited = SshKeys.ParseLine("from=\"10.0.0.1\" " + Rsa3072).Key!;

        CollectionAssert.Contains(SshKeys.Notes(weak, "deploy", now), "RSA 1024 bits: refused by OpenSSH");
        CollectionAssert.Contains(SshKeys.Notes(SshKeys.ParseLine(Rsa2048).Key!, "deploy", now), "RSA 2048 bits: weak");
        CollectionAssert.Contains(SshKeys.Notes(expired, "deploy", now), "expired 2025-01-01");
        CollectionAssert.Contains(SshKeys.Notes(SshKeys.ParseLine(Ed25519).Key!, "root", now), "root's key without from=");
        Assert.AreEqual(0, SshKeys.Notes(limited, "root", now).Count);
    }

    [TestMethod]
    public void Removing_a_key_takes_every_line_with_it_and_keeps_the_rest()
    {
        var file = $"# keys\n{Ed25519}\n{Rsa2048}\nno-pty {Ed25519}\n\n";

        var (text, removed) = SshKeys.Without(file, Ed25519Fingerprint);

        Assert.AreEqual(2, removed);
        Assert.AreEqual($"# keys\n{Rsa2048}\n\n", text);
    }

    [TestMethod]
    [DataRow("SHA256:bwW9qmkf4O+xf0OXE6Io4ilyh5Ox/o9pacDwKjfqvWs", Ed25519Fingerprint)]
    [DataRow("bwW9qmkf4O+xf0OXE6Io4ilyh5Ox/o9pacDwKjfqvWs=", Ed25519Fingerprint)]
    [DataRow("MD5:12:34", null)]
    public void Fingerprints_are_normalised(string typed, string? expected) =>
        Assert.AreEqual(expected, SshKeys.NormalizeFingerprint(typed));
}
