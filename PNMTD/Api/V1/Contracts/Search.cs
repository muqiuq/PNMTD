using PNMTD.Lib.Models.Enum;

namespace PNMTD.Api.V1.Contracts;

/// <summary><c>GET /search?q=</c>: hosts and sensors whose name contains q, for the global search.</summary>
public sealed record SearchResult(IReadOnlyList<SearchHost> Hosts, IReadOnlyList<SearchSensor> Sensors);

public sealed record SearchHost(Guid Id, string Name, string? Location, bool Enabled);

public sealed record SearchSensor(Guid Id, string Name, SensorType Type, Guid HostId, string HostName);
