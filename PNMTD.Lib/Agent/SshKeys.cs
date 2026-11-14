using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PNMTD.Lib.Agent;

/// <summary>A line of an authorized_keys file, or a pasted public key: its options, type, the key (Base64), its comment, bits and fingerprint.</summary>
public sealed record SshPublicKey(IReadOnlyList<string> Options, string Type, string Key, string? Comment, int? Bits, string Fingerprint)
{
    /// <summary>The key as a .pub file has it: type, key and comment, without options.</summary>
    public string OpenSshText => Comment is null ? $"{Type} {Key}" : $"{Type} {Key} {Comment}";
}

/// <summary>Why a line is not a key: empty or a comment (not counted), too long, a private key, or not readable.</summary>
public enum SshKeyLineProblem { None, Blank, TooLong, PrivateKey, Unreadable }

/// <summary>
/// authorized_keys lines and public keys (docs/linux-sensor-plan.md §3.4, §6.1), the same on PNMTD and on the agent: a fixed
/// grammar with length limits (sshd(8), AUTHORIZED_KEYS FILE FORMAT), fingerprints as <c>ssh-keygen -l</c> prints them
/// (<c>SHA256:</c> and unpadded Base64), and private keys recognised, so a pasted one is refused before anything stores it.
/// </summary>
public static class SshKeys
{
    /// <summary>OpenSSH's own limit for a public key's line.</summary>
    public const int MaxLineChars = 16 * 1024;

    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxKeysPerFile = 1000;
    public const int MaxKeysPerServer = 20_000;

    private static readonly string[] KeyTypes =
    [
        "ssh-ed25519", "ssh-rsa", "ssh-dss", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521",
        "sk-ecdsa-sha2-nistp256@openssh.com", "sk-ssh-ed25519@openssh.com",
        "ssh-ed25519-cert-v01@openssh.com", "ssh-rsa-cert-v01@openssh.com", "ssh-dss-cert-v01@openssh.com",
        "ecdsa-sha2-nistp256-cert-v01@openssh.com", "ecdsa-sha2-nistp384-cert-v01@openssh.com", "ecdsa-sha2-nistp521-cert-v01@openssh.com",
        "sk-ecdsa-sha2-nistp256-cert-v01@openssh.com", "sk-ssh-ed25519-cert-v01@openssh.com",
    ];

    public static bool IsKeyType(string? type) => type is not null && KeyTypes.Contains(type, StringComparer.Ordinal);

    /// <summary>Whether a text holds a private key (OpenSSH, PEM, PKCS#8, PuTTY, SSH2): refused wherever keys are pasted or read.</summary>
    public static bool ContainsPrivateKey(string? text) =>
        text is not null && (text.Contains("PRIVATE KEY-----", StringComparison.Ordinal)
            || text.Contains("PuTTY-User-Key-File-", StringComparison.Ordinal)
            || text.Contains("BEGIN SSH2 ENCRYPTED PRIVATE KEY", StringComparison.Ordinal));

    /// <summary>One line: a key, or why not. Blank lines and comments are <see cref="SshKeyLineProblem.Blank"/>.</summary>
    public static (SshPublicKey? Key, SshKeyLineProblem Problem) ParseLine(string? line)
    {
        var text = line?.Trim() ?? "";
        if (text.Length == 0 || text.StartsWith('#'))
        {
            return (null, SshKeyLineProblem.Blank);
        }
        if (text.Length > MaxLineChars)
        {
            return (null, SshKeyLineProblem.TooLong);
        }
        if (ContainsPrivateKey(text) || text.StartsWith("-----BEGIN", StringComparison.Ordinal))
        {
            return (null, SshKeyLineProblem.PrivateKey);
        }
        var options = new List<string>();
        var rest = text;
        var first = FirstToken(text);
        if (!IsKeyType(first))
        {
            if (!TryReadOptions(text, options, out var afterOptions))
            {
                return (null, SshKeyLineProblem.Unreadable);
            }
            rest = afterOptions.TrimStart();
        }
        var parts = rest.Split([' ', '\t'], 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !IsKeyType(parts[0]))
        {
            return (null, SshKeyLineProblem.Unreadable);
        }
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return (null, SshKeyLineProblem.Unreadable);
        }
        if (ReadString(blob, 0, out _) is not { } embedded || embedded != parts[0])
        {
            return (null, SshKeyLineProblem.Unreadable);
        }
        var comment = parts.Length > 2 ? parts[2].Trim() : null;
        return (new SshPublicKey(options, parts[0], parts[1], string.IsNullOrEmpty(comment) ? null : comment, Bits(parts[0], blob), Fingerprint(blob)),
            SshKeyLineProblem.None);
    }

    /// <summary>One or more public keys pasted (as in <c>id_ed25519.pub</c>): each line's key, or its problem with its number.</summary>
    public static IReadOnlyList<(int Line, SshPublicKey? Key, SshKeyLineProblem Problem)> ParseMany(string? text)
    {
        if (ContainsPrivateKey(text))
        {
            return [(1, null, SshKeyLineProblem.PrivateKey)];
        }
        var result = new List<(int, SshPublicKey?, SshKeyLineProblem)>();
        var number = 0;
        foreach (var line in (text ?? "").Split('\n'))
        {
            number++;
            var (key, problem) = ParseLine(line);
            if (problem != SshKeyLineProblem.Blank)
            {
                result.Add((number, key, problem));
            }
        }
        return result;
    }

    /// <summary>The fingerprint of a key's blob: <c>SHA256:</c> and the hash in Base64 without padding.</summary>
    public static string Fingerprint(byte[] blob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    public static string? Fingerprint(string base64Key)
    {
        try
        {
            return Fingerprint(Convert.FromBase64String(base64Key));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A fingerprint as typed or pasted: with or without <c>SHA256:</c>, without padding. Null when it isn't one.</summary>
    public static string? NormalizeFingerprint(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            t = t[7..];
        }
        t = t.TrimEnd('=');
        return t.Length == 43 && t.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/') ? "SHA256:" + t : null;
    }

    /// <summary>A short form for lists: <c>SHA256:Kq1f9hJ0…rS2A</c>.</summary>
    public static string Short(string fingerprint) => fingerprint.Length > 22 ? $"{fingerprint[..16]}…{fingerprint[^4..]}" : fingerprint;

    /// <summary>The key's size: the modulus for RSA, p for DSA, the curve for ECDSA, 256 for Ed25519 and the security keys.</summary>
    public static int? Bits(string type, byte[] blob)
    {
        var baseType = type.Replace("-cert-v01@openssh.com", "", StringComparison.Ordinal);
        switch (baseType)
        {
            case "ssh-ed25519":
            case "sk-ssh-ed25519@openssh.com":
            case "sk-ecdsa-sha2-nistp256@openssh.com":
            case "ecdsa-sha2-nistp256":
                return 256;
            case "ecdsa-sha2-nistp384":
                return 384;
            case "ecdsa-sha2-nistp521":
                return 521;
            case "ssh-rsa":
            case "ssh-dss":
                if (type != baseType)
                {
                    return null;
                }
                // ssh-rsa: string type, mpint e, mpint n. ssh-dss: string type, mpint p, …
                var offset = Skip(blob, 0);
                if (offset < 0)
                {
                    return null;
                }
                if (baseType == "ssh-rsa")
                {
                    offset = Skip(blob, offset);
                    if (offset < 0)
                    {
                        return null;
                    }
                }
                return MpintBits(blob, offset);
            default:
                return null;
        }
    }

    /// <summary>Weak or refused, from the type and size (§6.1): DSA and RSA under 2048 bits are refused by OpenSSH, RSA under 3072 is weak.</summary>
    public static string? Weakness(string type, int? bits) => type.StartsWith("ssh-dss", StringComparison.Ordinal) ? "DSA: refused by OpenSSH"
        : type.StartsWith("ssh-rsa", StringComparison.Ordinal) && bits is < 2048 ? $"RSA {bits} bits: refused by OpenSSH"
        : type.StartsWith("ssh-rsa", StringComparison.Ordinal) && bits is < 3072 ? $"RSA {bits} bits: weak"
        : null;

    /// <summary>
    /// The notes of §6.1 that a key has on its own: weak or refused, an expiry passed, root's key without <c>from=</c>, a CA key.
    /// Twice in a file and a cluster node's key need the file, so the agent adds them.
    /// </summary>
    public static List<string> Notes(SshPublicKey key, string account, DateTimeOffset now)
    {
        var notes = new List<string>();
        if (Weakness(key.Type, key.Bits) is { } weak)
        {
            notes.Add(weak);
        }
        if (Option(key.Options, "expiry-time") is { } expiry && ParseExpiry(expiry) is { } until && until < now)
        {
            notes.Add($"expired {until:yyyy-MM-dd}");
        }
        if (account == "root" && Option(key.Options, "from") is null && !HasOption(key.Options, "cert-authority"))
        {
            notes.Add("root's key without from=");
        }
        if (HasOption(key.Options, "cert-authority"))
        {
            notes.Add("a CA: certificates it signs get in");
        }
        return notes;
    }

    /// <summary>An option's value (<c>from="10.0.0.0/8"</c> → <c>10.0.0.0/8</c>), or null.</summary>
    public static string? Option(IReadOnlyList<string> options, string name)
    {
        foreach (var option in options)
        {
            var eq = option.IndexOf('=');
            if (eq > 0 && option[..eq].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var value = option[(eq + 1)..];
                return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Replace("\\\"", "\"") : value;
            }
        }
        return null;
    }

    public static bool HasOption(IReadOnlyList<string> options, string name) =>
        options.Any(o => o.Equals(name, StringComparison.OrdinalIgnoreCase) || o.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));

    /// <summary><c>expiry-time</c>: YYYYMMDD[HHMM[SS]], local time unless it ends in Z.</summary>
    public static DateTimeOffset? ParseExpiry(string value)
    {
        var utc = value.EndsWith('Z');
        var digits = utc ? value[..^1] : value;
        string[] formats = ["yyyyMMdd", "yyyyMMddHHmm", "yyyyMMddHHmmss"];
        return DateTime.TryParseExact(digits, formats, CultureInfo.InvariantCulture, utc ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal : DateTimeStyles.AssumeLocal,
            out var at) ? new DateTimeOffset(utc ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : at) : null;
    }

    /// <summary>The line without one key's lines (every line with that fingerprint, whatever its options): what a removal writes.</summary>
    public static (string Text, int Removed) Without(string text, string fingerprint)
    {
        var removed = 0;
        var kept = new StringBuilder();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var (key, _) = ParseLine(line.TrimEnd('\r'));
            if (key is not null && key.Fingerprint == fingerprint)
            {
                removed++;
                continue;
            }
            kept.Append(line);
            if (i < lines.Length - 1)
            {
                kept.Append('\n');
            }
        }
        return (kept.ToString(), removed);
    }

    // --- the grammar -----------------------------------------------------------------------------------------

    private static string FirstToken(string text)
    {
        var end = text.IndexOfAny([' ', '\t']);
        return end < 0 ? text : text[..end];
    }

    /// <summary>The options before the key type: comma-separated, double quotes may hold spaces, commas and <c>\"</c>.</summary>
    private static bool TryReadOptions(string text, List<string> options, out string rest)
    {
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '\\' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    current.Append("\\\"");
                    i++;
                    continue;
                }
                if (c == '"')
                {
                    quoted = false;
                }
                current.Append(c);
                continue;
            }
            switch (c)
            {
                case '"':
                    quoted = true;
                    current.Append(c);
                    break;
                case ',':
                    if (current.Length == 0)
                    {
                        rest = "";
                        return false;
                    }
                    options.Add(current.ToString());
                    current.Clear();
                    break;
                case ' ' or '\t':
                    if (current.Length == 0)
                    {
                        rest = "";
                        return false;
                    }
                    options.Add(current.ToString());
                    rest = text[i..];
                    return options.Count <= 64 && options.All(o => o.Length <= 4096);
                default:
                    current.Append(c);
                    break;
            }
        }
        rest = "";
        return false;
    }

    private static string? ReadString(byte[] blob, int offset, out int next)
    {
        next = -1;
        if (offset + 4 > blob.Length)
        {
            return null;
        }
        var length = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(offset));
        if (length < 0 || length > 256 || offset + 4 + length > blob.Length)
        {
            return null;
        }
        next = offset + 4 + length;
        return Encoding.ASCII.GetString(blob, offset + 4, length);
    }

    private static int Skip(byte[] blob, int offset)
    {
        if (offset + 4 > blob.Length)
        {
            return -1;
        }
        var length = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(offset));
        return length < 0 || offset + 4 + length > blob.Length ? -1 : offset + 4 + length;
    }

    private static int? MpintBits(byte[] blob, int offset)
    {
        if (offset + 4 > blob.Length)
        {
            return null;
        }
        var length = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(offset));
        if (length <= 0 || offset + 4 + length > blob.Length)
        {
            return null;
        }
        var start = offset + 4;
        var end = start + length;
        while (start < end && blob[start] == 0)
        {
            start++;
        }
        if (start == end)
        {
            return 0;
        }
        var bits = (end - start - 1) * 8;
        var top = blob[start];
        while (top != 0)
        {
            bits++;
            top >>= 1;
        }
        return bits;
    }
}
