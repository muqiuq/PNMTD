using System.Text.Json;
using System.Text.Json.Nodes;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Redmine;
using PNMTD.Tools;

namespace PNMTD.Cookie;

/// <summary>
/// Cookie's selection of the shared catalog (docs/cookie-plan.md §3.5, docs/mcp-plan.md §6.1): the tools that read and
/// that Cookie was built with, and running one with its right checked in code, whatever the model asks. The tools
/// themselves are in <c>PNMTD/Tools/</c>.
/// </summary>
public static class CookieTools
{
    public const int MaxResultChars = 8000;

    public static readonly IReadOnlyList<ToolSpec> All = [.. ToolCatalog.All.Where(t => t.ForCookie && t.Kind == ToolKind.Read)];

    /// <summary>The tools these rights allow; never one that writes (§3.5).</summary>
    public static IReadOnlyList<ToolSpec> For(UserRights rights) => [.. ToolCatalog.ForCookie(rights)];

    public static JsonArray Definitions(IEnumerable<ToolSpec> tools) => new([.. tools.Select(t => (JsonNode)new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.InputSchema.DeepClone() },
    })]);

    /// <summary>Runs a tool the model asked for: unknown, not allowed or failing comes back as an error the model can read.</summary>
    public static async Task<string> RunAsync(string name, string arguments, ToolContext ctx, CancellationToken ct)
    {
        var tool = For(ctx.Rights).FirstOrDefault(t => t.Name == name);
        if (tool is null)
        {
            return Error(All.Any(t => t.Name == name) ? $"{name} needs a right Cookie doesn't have here." : $"There is no tool {name}.");
        }
        JsonElement args;
        try
        {
            args = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments).RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error("The arguments are not JSON.");
        }
        try
        {
            await using var scope = ctx.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PnmtdDbContext>();
            var scoped = new ToolContext(scope.ServiceProvider, ctx.Rights, ctx.Privacy);
            var result = await tool.Run(args, scoped, db, ct);
            ctx.Areas.UnionWith(scoped.Areas);
            if (tool.Area is { } area)
            {
                ctx.Areas.Add(area.ToString());
            }
            return ToolResults.Serialize(result, MaxResultChars).Text;
        }
        catch (ToolException e)
        {
            return Error(e.Message);
        }
        catch (RedmineException e)
        {
            return Error($"Redmine: {e.Message}");
        }
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, ToolResults.Json);
}
