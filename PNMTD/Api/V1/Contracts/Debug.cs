namespace PNMTD.Api.V1.Contracts;

/// <summary>What the debug tools would use (docs/ai-operations-plan.md §8.2): never a password or a whole key.</summary>
public sealed record DebugStatus(DebugPushoverStatus Pushover, DebugMailStatus Mail, IReadOnlyList<Ref> PushoverRules);

/// <summary><c>ApiKeyEnd</c>: the API key's last 4 characters, when it is set.</summary>
public sealed record DebugPushoverStatus(bool Configured, string? ApiKeyEnd, string Endpoint);

public sealed record DebugMailStatus(bool Configured, string? Host, int Port, string? Sender, string? Username, bool PasswordSet);

/// <summary>A test Pushover: to a notification rule's user key (<c>RuleId</c>), or to a key typed in (<c>UserKey</c>).</summary>
public sealed class DebugPushoverRequest
{
    public Guid? RuleId { get; init; }

    public string? UserKey { get; init; }

    public string? Device { get; init; }

    /// <summary>−1, 0 or 1. Emergency (2) needs acknowledgements, and is left out.</summary>
    public int Priority { get; init; }

    public string? Title { get; init; }

    public string? Message { get; init; }
}

public sealed class DebugMailRequest
{
    public string? Recipient { get; init; }

    public string? Subject { get; init; }

    public string? Body { get; init; }
}

/// <summary>The provider's answer. <c>Target</c> names a rule or "a typed key", never the key.</summary>
public sealed record DebugResult(bool Ok, string Provider, string Target, string Detail, int DurationMs, DateTime At);
