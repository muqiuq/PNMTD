using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;

namespace PNMTD.Auth.Rights;

/// <summary>
/// An endpoint needs this level in this area, or the admin flag (docs/audit-redmine-zabbix-plan.md §5.4). On a controller:
/// the default of its actions; on an action, the action's own. Two of them on one action are both needed.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireRightAttribute(Area area, Level level) : AuthorizeAttribute(RightPolicies.Name([(area, level)]))
{
    public Area Area { get; } = area;

    public Level Level { get; } = level;
}

/// <summary>
/// Any of two or three rights: an upload serves the knowledge base, locations and responsibilities alike (§5.2,
/// docs/responsibilities-plan.md §12.1).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireAnyRightAttribute : AuthorizeAttribute
{
    public RequireAnyRightAttribute(Area area, Level level, Area otherArea, Level otherLevel)
        : base(RightPolicies.Name([(area, level), (otherArea, otherLevel)]))
    {
    }

    public RequireAnyRightAttribute(Area area, Level level, Area secondArea, Level secondLevel, Area thirdArea, Level thirdLevel)
        : base(RightPolicies.Name([(area, level), (secondArea, secondLevel), (thirdArea, thirdLevel)]))
    {
    }
}

/// <summary>Only the admin flag: users, groups and the flag itself (§5.1).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireAdminAttribute() : AuthorizeAttribute(RightPolicies.Admin);

/// <summary>A right an endpoint needs: one of <see cref="AnyOf"/>. The admin flag meets every one.</summary>
public sealed class RightRequirement(IReadOnlyList<(Area Area, Level Level)> anyOf) : IAuthorizationRequirement
{
    public IReadOnlyList<(Area Area, Level Level)> AnyOf { get; } = anyOf;

    public override string ToString() => string.Join(" or ", AnyOf.Select(r => RightsCatalog.Name(r.Area, r.Level)));
}

public sealed class AdminRequirement : IAuthorizationRequirement
{
    public override string ToString() => "admin";
}

public static class RightPolicies
{
    public const string Admin = "admin";

    private const string Prefix = "right:";

    public static string Name(IReadOnlyList<(Area Area, Level Level)> anyOf) =>
        Prefix + string.Join("|", anyOf.Select(r => RightsCatalog.Name(r.Area, r.Level)));

    public static AuthorizationPolicy? Build(string name)
    {
        if (name == Admin)
        {
            return new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new AdminRequirement()).Build();
        }
        if (!name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }
        var anyOf = new List<(Area, Level)>();
        foreach (var part in name[Prefix.Length..].Split('|'))
        {
            if (!RightsCatalog.TryParse(part, out var area, out var level))
            {
                throw new InvalidOperationException($"No such right: {part} (docs/audit-redmine-zabbix-plan.md §5.2).");
            }
            anyOf.Add((area, level));
        }
        return new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new RightRequirement(anyOf)).Build();
    }
}

/// <summary>Turns <c>right:Monitoring:Edit</c> and <c>admin</c> into policies; everything else as configured.</summary>
public sealed class RightPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    private readonly Dictionary<string, AuthorizationPolicy> built = [];
    private readonly Lock gate = new();

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        lock (gate)
        {
            if (built.TryGetValue(policyName, out var known))
            {
                return known;
            }
        }
        if (RightPolicies.Build(policyName) is { } policy)
        {
            lock (gate)
            {
                built[policyName] = policy;
            }
            return policy;
        }
        return await base.GetPolicyAsync(policyName);
    }
}

/// <summary>Checks a right against the request's rights (<see cref="UserRights"/>).</summary>
public sealed class RightHandler : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        var rights = UserRights.Of(context.User);
        foreach (var requirement in context.PendingRequirements.ToList())
        {
            switch (requirement)
            {
                case AdminRequirement when rights.IsAdmin:
                    context.Succeed(requirement);
                    break;
                case RightRequirement right when right.AnyOf.Any(r => rights.Has(r.Area, r.Level)):
                    context.Succeed(requirement);
                    break;
            }
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// A refusal on /api says which right is missing (§5.4): 403 with <c>code: "missing_right"</c> and the area and level,
/// so that the UI can say "You need Monitoring: Edit". Everything else goes to the default handler.
/// </summary>
public sealed class MissingRightResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler inner = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && Api.ApiPaths.IsApi(context.Request.Path) && Missing(authorizeResult) is { } missing)
        {
            var (area, level) = missing is RightRequirement right ? right.AnyOf[0] : (default(Area?), default(Level?));
            var detail = missing is AdminRequirement ? "Only an admin may do this." : $"You need {RightsCatalog.Of(area!.Value).Label}: {level}.";
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: detail, extensions: new Dictionary<string, object?>
            {
                ["code"] = "missing_right",
                ["area"] = area?.ToString(),
                ["level"] = missing is AdminRequirement ? "Admin" : level?.ToString(),
            }).ExecuteAsync(context);
            return;
        }
        await inner.HandleAsync(next, context, policy, authorizeResult);
    }

    public static IAuthorizationRequirement? Missing(PolicyAuthorizationResult result) =>
        result.AuthorizationFailure?.FailedRequirements.FirstOrDefault(r => r is RightRequirement or AdminRequirement);
}
