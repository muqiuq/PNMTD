namespace PNMTD.SensorApi;

/// <summary>
/// The paths of the frozen sensor API. Matching is case-insensitive, as routing is (<c>/EVENT/…</c>
/// works). Nothing added for the UI or /api/v1 (security headers, ProblemDetails, the cookie scheme)
/// applies here (docs/webui-migration-plan.md §3.3).
/// </summary>
public static class SensorPaths
{
    public static bool IsSensorPath(PathString path) =>
        path.StartsWithSegments("/event", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/read", StringComparison.OrdinalIgnoreCase);
}
