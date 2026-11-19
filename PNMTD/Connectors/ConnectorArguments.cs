using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Connectors;

/// <summary>What JSON a parameter takes (§5.2): a string, a number, true or false. Secure parameters take a string.</summary>
public enum ConnectorValueKind { Text, Number, Bool, Secure }

/// <summary>A connector's choice for one parameter, as the checks use it (§5.1).</summary>
public sealed record ConnectorArgumentRule(string Name, ConnectorArgumentMode Mode, bool Required, string? Pattern, JsonElement? FixedValue,
    bool Multiline, ScriptParameter Parameter, string? Description, int Order)
{
    public ConnectorValueKind Kind => ConnectorArguments.KindOf(Parameter);
}

/// <summary>
/// A call's arguments checked (§5): the values to pass (the caller's and the fixed ones), the secure ones apart, the caller's plain
/// ones for the history, and every problem by argument. One problem refuses the call.
/// </summary>
public sealed class ConnectorArgumentCheck
{
    public Dictionary<string, JsonElement> Plain { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Secure { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, JsonElement> CallerPlain { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);

    public bool Ok => Fields.Count == 0;

    /// <summary>"2 arguments are not allowed."</summary>
    public string Message => Fields.Count == 1 ? "1 argument is not allowed." : $"{Fields.Count} arguments are not allowed.";

    public void Problem(string field, string message) => Fields.TryAdd(field, message);
}

/// <summary>A pattern compiled once (§5.3): the whole value must match; NonBacktracking where the pattern allows it, a timeout otherwise.</summary>
public static class ConnectorPatterns
{
    public const int MaxLength = 500;

    private static readonly ConcurrentDictionary<(string, int), (Regex? Regex, string? Error)> Cache = new();

    public static (Regex? Regex, string? Error) Compile(string pattern, int timeoutMs)
    {
        if (Cache.Count > 2000)
        {
            Cache.Clear();
        }
        return Cache.GetOrAdd((pattern, timeoutMs), key =>
        {
            var anchored = @"\A(?:" + key.Item1 + @")\z";
            try
            {
                return (new Regex(anchored, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking), null);
            }
            catch (NotSupportedException)
            {
                // Backreferences and lookarounds need backtracking: then with a timeout.
            }
            catch (ArgumentException e)
            {
                return (null, e.Message);
            }
            try
            {
                return (new Regex(anchored, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(Math.Clamp(key.Item2, 1, 5000))), null);
            }
            catch (ArgumentException e)
            {
                return (null, e.Message);
            }
        });
    }

    /// <summary>True or false, or null when the match took longer than the timeout.</summary>
    public static bool? Matches(Regex regex, string value)
    {
        try
        {
            return regex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}

/// <summary>
/// A connector's arguments (docs/connectors-plan.md §5): a call's values against the connector's choices (exposed, fixed, omitted),
/// their JSON types, the characters a value may hold, the patterns, and then the script's own checks; and the choices reconciled with
/// a new parse of the text (§5.5).
/// </summary>
public static class ConnectorArguments
{
    private static readonly JsonSerializerOptions Json = AgentJson.Options;

    public static ConnectorValueKind KindOf(ScriptParameter p)
    {
        if (p.IsSecure)
        {
            return ConnectorValueKind.Secure;
        }
        if (p.IsSwitch || p.Type.ToLowerInvariant() is "bool" or "boolean" or "switch" or "switchparameter")
        {
            return ConnectorValueKind.Bool;
        }
        return p.Type.ToLowerInvariant() is "int" or "int32" or "int64" or "long" or "double" or "decimal" or "byte" or "uint32" or "number" or "single" or "float"
            ? ConnectorValueKind.Number
            : ConnectorValueKind.Text;
    }

    public static ConnectorArgumentRule Rule(ConnectorArgumentEntity row) => new(row.Name, row.Mode, row.Required, row.Pattern,
        row.FixedValue is null ? null : JsonDocument.Parse(row.FixedValue).RootElement.Clone(), row.Multiline, Parameter(row), row.Description, row.Order);

    public static ScriptParameter Parameter(ConnectorArgumentEntity row) =>
        JsonSerializer.Deserialize<ScriptParameter>(row.Parameter, Json) ?? new ScriptParameter(row.Name, "string", false, null, null, null, null, null, null, false, false);

    public static string ParameterJson(ScriptParameter p) => JsonSerializer.Serialize(p, Json);

    /// <summary>
    /// A call's arguments (§5.1 to §5.4) as the caller sent them, secure ones included (as strings): every problem by argument. With
    /// the checks passed, the values to pass are <see cref="ConnectorArgumentCheck.Plain"/> and <see cref="ConnectorArgumentCheck.Secure"/>.
    /// </summary>
    public static ConnectorArgumentCheck Check(IReadOnlyList<ConnectorArgumentRule> rules, IReadOnlyDictionary<string, JsonElement> given,
        int maxValueChars, int patternTimeoutMs)
    {
        var result = new ConnectorArgumentCheck();
        var byName = rules.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in given)
        {
            if (!byName.TryGetValue(name, out var rule) || rule.Mode == ConnectorArgumentMode.Omitted)
            {
                result.Problem(name, "not an argument of this connector");
                continue;
            }
            if (rule.Mode == ConnectorArgumentMode.Fixed)
            {
                result.Problem(rule.Name, "set by PNMT: it can't be passed");
                continue;
            }
            if (!seen.Add(rule.Name))
            {
                result.Problem(rule.Name, "passed twice");
                continue;
            }
            if (ValueProblem(rule, value, maxValueChars, patternTimeoutMs) is { } problem)
            {
                result.Problem(rule.Name, problem);
                continue;
            }
            if (rule.Kind == ConnectorValueKind.Secure)
            {
                result.Secure[rule.Parameter.Name] = value.GetString()!;
            }
            else
            {
                result.Plain[rule.Parameter.Name] = value.Clone();
                result.CallerPlain[rule.Parameter.Name] = value.Clone();
            }
        }
        foreach (var rule in rules.Where(r => r.Mode == ConnectorArgumentMode.Exposed && (r.Required || r.Parameter.Mandatory) && !seen.Contains(r.Name)))
        {
            result.Problem(rule.Name, "required");
        }
        foreach (var rule in rules.Where(r => r.Mode == ConnectorArgumentMode.Fixed && r.FixedValue is not null))
        {
            result.Plain[rule.Parameter.Name] = rule.FixedValue!.Value;
        }
        if (result.Ok)
        {
            // The script's own checks (§5.2): mandatory parameters, sets, ranges and numbers, on what will be passed.
            foreach (var (field, message) in ScriptArguments.Check(rules.Select(r => r.Parameter).ToList(), result.Plain, result.Secure.Keys.ToList()))
            {
                result.Problem(field.StartsWith("arguments.", StringComparison.Ordinal) ? field["arguments.".Length..] : field, Lower(message));
            }
        }
        return result;
    }

    /// <summary>Why a value isn't allowed for an argument, never repeating it; null when it is.</summary>
    public static string? ValueProblem(ConnectorArgumentRule rule, JsonElement value, int maxValueChars, int patternTimeoutMs)
    {
        if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            return "arrays and objects aren't allowed";
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return "null isn't a value: leave the argument out";
        }
        string text;
        switch (rule.Kind)
        {
            case ConnectorValueKind.Bool:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return "must be true or false";
                }
                text = value.ValueKind == JsonValueKind.True ? "true" : "false";
                break;
            case ConnectorValueKind.Number:
                if (value.ValueKind != JsonValueKind.Number)
                {
                    return "must be a number";
                }
                text = value.TryGetDecimal(out var d) ? d.ToString(CultureInfo.InvariantCulture) : value.GetRawText();
                break;
            default:
                if (value.ValueKind != JsonValueKind.String)
                {
                    return "must be a string";
                }
                text = value.GetString() ?? "";
                if (CharactersProblem(text, rule.Multiline && rule.Kind == ConnectorValueKind.Text, maxValueChars) is { } characters)
                {
                    return characters;
                }
                break;
        }
        if (rule.Pattern is { Length: > 0 } pattern)
        {
            var (regex, error) = ConnectorPatterns.Compile(pattern, patternTimeoutMs);
            if (regex is null)
            {
                return $"the connector's pattern doesn't compile ({error})";
            }
            switch (ConnectorPatterns.Matches(regex, text))
            {
                case null:
                    return $"the pattern {pattern} took too long to match (pattern_timeout)";
                case false:
                    return $"doesn't match the pattern {pattern}";
            }
        }
        return null;
    }

    /// <summary>
    /// The characters of a text value (§5.2): its length, no control characters (line breaks and tabs only for a multi-line argument),
    /// no invisible or bidi characters, no broken surrogates.
    /// </summary>
    public static string? CharactersProblem(string text, bool multiline, int maxValueChars)
    {
        if (text.Length > maxValueChars)
        {
            return $"longer than {maxValueChars} characters";
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }
                return "isn't valid UTF-8";
            }
            if (char.IsLowSurrogate(c))
            {
                return "isn't valid UTF-8";
            }
            if (c < 0x20 || c is >= '\u007F' and <= '\u009F')
            {
                var allowed = multiline && (c is '\n' or '\t' || (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n'));
                if (!allowed)
                {
                    return c is '\n' or '\r' or '\t'
                        ? "holds a line break or a tab, which this argument doesn't allow"
                        : "holds a control character";
                }
                continue;
            }
            if (c is >= '\u200B' and <= '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069')
            {
                return "holds an invisible or a direction character";
            }
        }
        return null;
    }

    /// <summary>"Site is mandatory." → "mandatory" style, for the 422's fields: the field name is the key already.</summary>
    private static string Lower(string message)
    {
        var trimmed = message.TrimEnd('.');
        return trimmed.Length > 0 ? char.ToLowerInvariant(trimmed[0]) + trimmed[1..] : trimmed;
    }

    // --- the choices ---------------------------------------------------------------------------------------------------------

    /// <summary>Why a choice doesn't fit its parameter (§5.5); null when it does.</summary>
    public static string? Misfit(ConnectorArgumentEntity row, ScriptParameter p, int patternTimeoutMs = 100)
    {
        var kind = KindOf(p);
        switch (row.Mode)
        {
            case ConnectorArgumentMode.Fixed when p.IsSecure:
                return $"{p.Name} is secure now, and a secure parameter can't be fixed";
            case ConnectorArgumentMode.Fixed when row.FixedValue is null:
                return $"{p.Name} is fixed without a value";
            case ConnectorArgumentMode.Fixed:
                var value = JsonDocument.Parse(row.FixedValue!).RootElement;
                var rule = new ConnectorArgumentRule(row.Name, row.Mode, false, null, value, row.Multiline, p, null, 0);
                if (ValueProblem(rule, value, int.MaxValue, patternTimeoutMs) is { } problem)
                {
                    return $"{p.Name}'s fixed value {problem}";
                }
                var dictionary = new Dictionary<string, JsonElement> { [p.Name] = value };
                if (ScriptArguments.Check([p with { Mandatory = false }], dictionary, []).FirstOrDefault() is { Message: { } message })
                {
                    return $"{p.Name}'s fixed value: {Lower(message)}";
                }
                return null;
            case ConnectorArgumentMode.Omitted when p.Mandatory:
                return $"{p.Name} is mandatory now: expose it or fix it";
            case ConnectorArgumentMode.Exposed when row.Pattern is { Length: > 0 } && kind == ConnectorValueKind.Bool:
                return $"{p.Name} is a switch now, and a pattern on true or false says nothing";
            default:
                return null;
        }
    }

    /// <summary>What a reconcile changes (§5.5), for the form before the save and for the audit.</summary>
    public sealed record Reconciled(IReadOnlyList<ConnectorArgumentEntity> Added, IReadOnlyList<ConnectorArgumentEntity> Removed,
        IReadOnlyList<string> Misfits, IReadOnlyList<string> Changes)
    {
        public bool NeedsReview => Misfits.Count > 0;
    }

    /// <summary>
    /// The choices follow a parse's parameters by name (§5.5): kept for parameters that stayed, a new mandatory one exposed and a new
    /// optional one omitted, the rows of parameters that are gone removed. A choice that no longer fits is listed in Misfits.
    /// </summary>
    public static Reconciled Reconcile(Guid connectorId, List<ConnectorArgumentEntity> rows, IReadOnlyList<ScriptParameter> parameters, int patternTimeoutMs = 100)
    {
        var added = new List<ConnectorArgumentEntity>();
        var removed = new List<ConnectorArgumentEntity>();
        var misfits = new List<string>();
        var changes = new List<string>();
        var byName = rows.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            if (byName.Remove(p.Name, out var row))
            {
                row.Name = p.Name;
                row.Order = i;
                row.Parameter = ParameterJson(p);
                if (p.Mandatory && row.Mode == ConnectorArgumentMode.Exposed)
                {
                    row.Required = true;
                }
                if (row.Multiline && KindOf(p) != ConnectorValueKind.Text)
                {
                    row.Multiline = false;
                }
                if (Misfit(row, p, patternTimeoutMs) is { } misfit)
                {
                    misfits.Add(misfit);
                }
                continue;
            }
            var created = New(connectorId, p, i);
            added.Add(created);
            changes.Add($"{p.Name} is new: {(created.Mode == ConnectorArgumentMode.Exposed ? "exposed, because it is mandatory" : "omitted")}");
        }
        foreach (var gone in byName.Values)
        {
            removed.Add(gone);
            changes.Add($"{gone.Name} is gone from the script: its row goes");
        }
        rows.RemoveAll(removed.Contains);
        rows.AddRange(added);
        return new Reconciled(added, removed, misfits, changes);
    }

    /// <summary>A parameter's first choice (§5.1): mandatory ones exposed and required, the others omitted.</summary>
    public static ConnectorArgumentEntity New(Guid connectorId, ScriptParameter p, int order) => new()
    {
        Id = Guid.NewGuid(), ConnectorId = connectorId, Name = p.Name, Mode = p.Mandatory ? ConnectorArgumentMode.Exposed : ConnectorArgumentMode.Omitted,
        Required = p.Mandatory, Order = order, Parameter = ParameterJson(p), Description = p.HelpMessage,
    };
}
