using System.Text.Json.Serialization;

namespace PNMTD.Models.Enums;

/// <summary>What a scan run covers (docs/network-map-plan.md §7.1): runs of one router go first, then locations, then all.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NetworkScanScope>))]
public enum NetworkScanScope : short
{
    Router = 0,
    Location = 1,
    All = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<NetworkScanTrigger>))]
public enum NetworkScanTrigger : short
{
    Manual = 0,
    Schedule = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter<NetworkScanState>))]
public enum NetworkScanState : short
{
    Queued = 0,
    Running = 1,
    Done = 2,
    Cancelled = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<NetworkScanItemState>))]
public enum NetworkScanItemState : short
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4,
    Cancelled = 5,
}

/// <summary>How a connection came to be: made by hand, or a suggestion a person accepted (§3.1).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NetworkConnectionSource>))]
public enum NetworkConnectionSource : short
{
    Manual = 0,
    Suggestion = 1,
}

/// <summary>§5.4: open until a person decides; dismissed stays dismissed while the same key comes back.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NetworkSuggestionState>))]
public enum NetworkSuggestionState : short
{
    Open = 0,
    Accepted = 1,
    Dismissed = 2,
}

/// <summary>Where a skip rule applies (docs/network-suggestion-rules-plan.md §4.1).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NetworkSkipRuleScope>))]
public enum NetworkSkipRuleScope : short
{
    All = 0,
    Location = 1,
    Router = 2,
}
