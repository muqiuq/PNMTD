namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// A stored setting or task state. Keys are lower-case and unique. Read-only entries are written by
/// the background tasks; <see cref="Description"/> explains the keys PNMTD knows.
/// </summary>
public sealed record KeyValueItem(Guid Id, string Key, string Value, string Note, bool Enabled, bool IsReadOnly, string? Description);

/// <summary>POST and PUT <c>/key-values</c>. A read-only entry cannot be changed (409 read_only).</summary>
public sealed class KeyValueRequest
{
    public string? Key { get; init; }

    public string? Value { get; init; }

    public string? Note { get; init; }

    public bool Enabled { get; init; } = true;
}
