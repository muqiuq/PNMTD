using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;

namespace PNMTD.Ai;

/// <summary>A line that looks like it holds a credential (1-based), and what it looks like: one of <see cref="SecretKinds"/>. Never the text.</summary>
public sealed record SecretFinding(int Line, string Kind);

/// <summary>What a <see cref="SecretFinding"/> looks like.</summary>
public static class SecretKinds
{
    public const string PrivateKey = "privateKey";
    public const string Password = "password";
    /// <summary>A token in a known format: GitHub, GitLab, Slack, AWS, Google, Stripe, <c>sk-…</c>, PNMT's MCP tokens.</summary>
    public const string Token = "token";
    public const string Jwt = "jwt";
    public const string Authorization = "authorization";
    /// <summary>A long random string right after <i>key</i>, <i>secret</i> or <i>token</i>.</summary>
    public const string Secret = "secret";
}

/// <summary>
/// The credentials scan in code (docs/locations-knowledge-plan.md §7.3), before a page's text may leave PNMT, and for the
/// warning on saving (§5.7). It looks for private keys, <c>password: value</c> and the like (German included), the known
/// token formats, JWTs, <c>Authorization:</c> headers, passwords in URLs, and long random strings right after <i>key</i>,
/// <i>secret</i> or <i>token</i>. It is tuned to let pass what a knowledge base holds: a page about the password policy,
/// <c>--password-file</c>, placeholders (<c>&lt;password&gt;</c>, <c>${DB_PASSWORD}</c>, <c>****</c>), checksums, SSH
/// public keys and fingerprints, GUIDs. One finding per line, the first that applies. Like every heuristic it misses
/// some, which is why the two statements come first (§7.2).
/// </summary>
public static partial class SecretScanner
{
    /// <summary>Longer lines are scanned up to here: a credential is short, and the patterns stay fast.</summary>
    private const int MaxLineChars = 8192;

    public static IReadOnlyList<SecretFinding> Scan(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        var findings = new List<SecretFinding>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Length > MaxLineChars ? lines[i][..MaxLineChars] : lines[i];
            try
            {
                if (Kind(line) is { } kind)
                {
                    findings.Add(new SecretFinding(i + 1, kind));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological line: not a credential's shape.
            }
        }
        return findings;
    }

    private static string? Kind(string line)
    {
        if (line.Length < 8)
        {
            return null;
        }
        // The block's first line; its body and its END line are the same key.
        if (SshKeys.ContainsPrivateKey(line) && !line.Contains("-----END", StringComparison.Ordinal))
        {
            return SecretKinds.PrivateKey;
        }
        if (KnownToken().IsMatch(line))
        {
            return SecretKinds.Token;
        }
        if (Jwt().IsMatch(line))
        {
            return SecretKinds.Jwt;
        }
        if (AuthorizationHeader().IsMatch(line))
        {
            return SecretKinds.Authorization;
        }
        if (UrlPassword().IsMatch(line))
        {
            return SecretKinds.Password;
        }
        foreach (Match m in PasswordAssignment().Matches(line))
        {
            if (IsValue(m.Groups["value"].Value, minLength: 4))
            {
                return SecretKinds.Password;
            }
        }
        if (TableRowPassword(line))
        {
            return SecretKinds.Password;
        }
        foreach (Match m in KeywordValue().Matches(line))
        {
            if (LooksRandom(m.Groups["value"].Value))
            {
                return SecretKinds.Secret;
            }
        }
        return null;
    }

    /// <summary>
    /// What follows <c>password:</c> is a password when it is one token (or quoted), long enough, and not a placeholder,
    /// a variable, a path or a word such as <i>none</i>. Prose (<i>see the password manager</i>) is not.
    /// </summary>
    private static bool IsValue(string raw, int minLength)
    {
        var value = TrailingComment().Replace(raw, "").Trim().TrimEnd(',', ';').Trim().Trim('*', '_').Trim();
        var quoted = value.Length >= 2 && (value[0] is '"' or '\'' or '`') && value[^1] == value[0];
        if (quoted)
        {
            value = value[1..^1];
        }
        else if (value.Any(char.IsWhiteSpace))
        {
            return false;
        }
        if (value.Length < minLength || value.Distinct().Count() == 1)
        {
            return false;
        }
        return !Placeholder().IsMatch(value);
    }

    /// <summary>A table row with a cell naming a password and a value in the next: <c>| Wi-Fi password | Sommer2024! |</c>.</summary>
    private static bool TableRowPassword(string line)
    {
        if (!line.TrimStart().StartsWith('|'))
        {
            return false;
        }
        var cells = line.Trim().Trim('|').Split('|');
        for (var i = 0; i + 1 < cells.Length; i++)
        {
            if (PasswordCell().IsMatch(cells[i].Trim().Trim('*', '_').Trim()) && IsValue(cells[i + 1], minLength: 4))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A key, not a word, an id or a public key: letters and digits, varied enough.</summary>
    private static bool LooksRandom(string value)
    {
        value = value.TrimEnd('.', '-', '_');
        if (value.Length < 20 || !value.Any(char.IsDigit) || !value.Any(char.IsLetter) || Guid.TryParse(value, out _)
            || value.StartsWith("AAAA", StringComparison.Ordinal))
        {
            return false;
        }
        var entropy = value.GroupBy(c => c).Sum(g =>
        {
            var p = (double)g.Count() / value.Length;
            return -p * Math.Log2(p);
        });
        return entropy >= 3.0;
    }

    [GeneratedRegex(@"\b(?:(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{22,}|glpat-[A-Za-z0-9_-]{20,}|xox[abposr]-[A-Za-z0-9-]{10,}"
        + @"|(?:AKIA|ASIA)[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35}|[sr]k_(?:live|test)_[A-Za-z0-9]{16,}|sk-(?=[A-Za-z0-9_-]*\d)[A-Za-z0-9_-]{20,}"
        // PNMT's own MCP tokens (docs/mcp-plan.md §9.5) and connector callers' keys (docs/connectors-plan.md §6.2).
        + @"|pnmt_(?:r[ow]|cx)_[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-]))(?:\b|(?<=[_-]))",
        RegexOptions.None, 250)]
    private static partial Regex KnownToken();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.None, 250)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(?:Authorization\s*:\s*(?:Bearer|Basic|Token|Digest|Negotiate|NTLM)\s+[A-Za-z0-9._~+/=-]{8,}"
        + @"|(?:X-Api-Key|Api-Key|X-Auth-Token)\s*:\s*[A-Za-z0-9._~+/=-]{16,})", RegexOptions.IgnoreCase, 250)]
    private static partial Regex AuthorizationHeader();

    /// <summary><c>https://user:secret@host</c>; not <c>${VAR}</c> or <c>&lt;password&gt;</c> as the password.</summary>
    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://[^/\s:@]+:(?![$<{*%])[^/\s@]{3,}@", RegexOptions.IgnoreCase, 250)]
    private static partial Regex UrlPassword();

    /// <summary>
    /// <c>password: x</c>, <c>DB_PASSWORD=x</c>, <c>"passwd": "x"</c>, <c>Passwort = x</c>, <c>Kennwort: x</c>. The keyword
    /// is followed by the colon or equals sign at once, so <c>--password-file=…</c>, <c>password_file:</c>,
    /// <c>passwordless:</c> and <c>password policy: …</c> aren't assignments.
    /// </summary>
    [GeneratedRegex(@"(?<![\p{L}\d])[\w.-]*?(?:password|passwd|passphrase|pwd|passwort|kennwort)[*_""'`]{0,3}[^\S\n]*[:=][*_]{0,3}[^\S\n]*(?<value>[^\n]+)",
        RegexOptions.IgnoreCase, 250)]
    private static partial Regex PasswordAssignment();

    /// <summary><i>key</i>, <i>secret</i>, <i>token</i> (<i>Schlüssel</i>), then at once, after an optional <c>:</c> or <c>=</c> and quote, a long string.</summary>
    [GeneratedRegex(@"(?<![\p{L}\d])(?:api[ _-]?key|access[ _-]?key|secret|token|key|schlüssel)(?![\p{L}\d])[*_""'`]{0,3}[^\S\n]*[:=]?[*_]{0,3}[^\S\n]*[""'`]?(?<value>[A-Za-z0-9+/=_.~-]{20,})",
        RegexOptions.IgnoreCase, 250)]
    private static partial Regex KeywordValue();

    [GeneratedRegex(@"^[\p{L}\d .-]*?(?:password|passwort|kennwort|passwd|pwd)$", RegexOptions.IgnoreCase, 250)]
    private static partial Regex PasswordCell();

    [GeneratedRegex(@"\s+(?:#|//).*$", RegexOptions.None, 250)]
    private static partial Regex TrailingComment();

    /// <summary>Not a password: <c>&lt;…&gt;</c>, <c>${…}</c>, <c>$VAR</c>, <c>%VAR%</c>, <c>{{ … }}</c>, <c>[redacted]</c>, a path, or a word that names no value.</summary>
    [GeneratedRegex(@"^(?:<.*>|\$\{.*\}|\$\(.*\)|\$[A-Za-z_][A-Za-z0-9_]*|%[A-Za-z_][A-Za-z0-9_]*%|\{\{.*\}\}|\[.*\]|\(.*\)|[*xX•.#_-]+|(?:~|\.{1,2})?/.*"
        + @"|none|null|nil|empty|unset|required|optional|true|false|yes|no|n/a|secret|password|passwort|kennwort|redacted|hidden|geheim)$",
        RegexOptions.IgnoreCase, 250)]
    private static partial Regex Placeholder();
}
