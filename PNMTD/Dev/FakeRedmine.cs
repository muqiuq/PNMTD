using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace PNMTD.Dev;

/// <summary>
/// A Redmine for the tests and for <c>pnmtd dev fake-integrations</c> (docs/audit-redmine-zabbix-plan.md §12), answering as
/// Redmine 6.0 did in R0 (the fixtures): its projects (IT Support with Printers below it, Network, and Other, which PNMT
/// doesn't show), statuses, priorities and tickets, with the filters PNMT sends applied. It records every request; it can
/// be down, or refuse the key.
/// </summary>
public sealed class FakeRedmine : HttpMessageHandler
{
    public const string Url = "http://redmine.test";
    public const string Key = "test-redmine-key-0123456789";

    public static readonly IReadOnlyDictionary<string, string?> Settings = new Dictionary<string, string?>
    {
        ["Redmine:Url"] = Url,
        ["Redmine:ApiKey"] = Key,
        ["Redmine:Projects"] = "it-support network",
        ["Redmine:PublicUrl"] = "https://redmine.example.com",
    };


    public List<HttpRequestMessage> Requests { get; } = [];

    public bool Down { get; set; }

    public bool RefuseKey { get; set; }

    /// <summary>The tickets, by number: those recorded in R0, and one of Printers, the subproject.</summary>
    public Dictionary<int, JsonObject> Issues { get; } = Load();

    public void Install(IServiceCollection services) =>
        services.AddHttpClient(PNMTD.Redmine.RedmineClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => this);

    private static Dictionary<int, JsonObject> Load()
    {
        var issues = new Dictionary<int, JsonObject>();
        foreach (var name in new[] { "issues_projects.json", "issues_by_id.json" })
        {
            foreach (var issue in JsonNode.Parse(Fixture.Read("redmine", name))!["issues"]!.AsArray())
            {
                issues[issue!["id"]!.GetValue<int>()] = issue.AsObject();
            }
        }
        var printers = JsonNode.Parse(issues[10].ToJsonString())!.AsObject();
        printers["id"] = 13;
        printers["subject"] = "Toner for the printer in 204";
        printers["project"] = new JsonObject { ["id"] = 2, ["name"] = "Printers" };
        printers["priority"] = new JsonObject { ["id"] = 1, ["name"] = "Low" };
        issues[13] = printers;
        return issues;
    }

    /// <summary>A request as Redmine would answer it, its path relative to Redmine's address.</summary>
    public Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken) => SendAsync(request, cancellationToken);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }
        if (Down)
        {
            throw new HttpRequestException("Connection refused (redmine.test:80)");
        }
        if (RefuseKey || request.Headers.TryGetValues("X-Redmine-API-Key", out var keys) is false || keys.Single() != Key)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
        var path = request.RequestUri!.AbsolutePath;
        var query = Parse(request.RequestUri.Query);
        return Task.FromResult(path switch
        {
            "/users/current.json" => File("users_current.json"),
            "/projects.json" => Json(new JsonObject
            {
                ["projects"] = new JsonArray(Project(1, "IT Support", "it-support", null), Project(2, "Printers", "it-printers", 1),
                    Project(3, "Network", "network", null), Project(4, "Other", "other", null)),
                ["total_count"] = 4, ["offset"] = 0, ["limit"] = 100,
            }),
            "/issue_statuses.json" => File("statuses.json"),
            "/trackers.json" => File("trackers.json"),
            "/enumerations/issue_priorities.json" => File("priorities.json"),
            var p when p.StartsWith("/projects/", StringComparison.Ordinal) && p.EndsWith("/memberships.json", StringComparison.Ordinal) => File("memberships.json"),
            "/issues.json" => List(query),
            var p when p.StartsWith("/issues/", StringComparison.Ordinal) && p.EndsWith(".json", StringComparison.Ordinal) => One(int.Parse(p[8..^5])),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
    }

    private HttpResponseMessage One(int id)
    {
        if (id == 9)
        {
            return File("issue_full.json");
        }
        return Issues.TryGetValue(id, out var issue) ? Json(new JsonObject { ["issue"] = JsonNode.Parse(issue.ToJsonString()) }) : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>Redmine's issue list: the filters PNMT uses (f[], op[], v[]; issue_id; sort; offset; limit).</summary>
    private HttpResponseMessage List(List<(string Key, string Value)> q)
    {
        IEnumerable<JsonObject> issues = Issues.Values;
        var plain = q.Where(p => !p.Key.StartsWith("f[", StringComparison.Ordinal) && !p.Key.StartsWith("op[", StringComparison.Ordinal)
            && !p.Key.StartsWith("v[", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value);
        var statusFilter = "o";
        if (plain.TryGetValue("issue_id", out var ids))
        {
            var wanted = ids.Split(',').Select(int.Parse).ToHashSet();
            issues = issues.Where(i => wanted.Contains(Id(i)));
        }
        if (plain.TryGetValue("status_id", out var status))
        {
            statusFilter = status == "*" ? "*" : "o";
        }
        foreach (var field in q.Where(p => p.Key == "f[]").Select(p => p.Value))
        {
            var op = q.First(p => p.Key == $"op[{field}]").Value;
            var values = q.Where(p => p.Key == $"v[{field}][]").Select(p => p.Value).ToList();
            switch (field)
            {
                case "status_id":
                    statusFilter = op;
                    break;
                case "project_id":
                    issues = issues.Where(i => values.Contains(i["project"]!["id"]!.ToString()));
                    break;
                case "priority_id":
                    issues = issues.Where(i => values.Contains(i["priority"]!["id"]!.ToString()));
                    break;
                case "tracker_id":
                    issues = issues.Where(i => values.Contains(i["tracker"]!["id"]!.ToString()));
                    break;
                case "assigned_to_id":
                    issues = op == "!*" ? issues.Where(i => i["assigned_to"] is null) : issues.Where(i => values.Contains(i["assigned_to"]?["id"]?.ToString() ?? ""));
                    break;
                case "due_date":
                    issues = issues.Where(i => i["due_date"]?.GetValue<string>() is { } d && string.CompareOrdinal(d, values[0]) <= 0);
                    break;
                case "updated_on":
                    issues = issues.Where(i => string.CompareOrdinal(i["updated_on"]!.GetValue<string>()[..10], values[0]) <= 0);
                    break;
                case "any_searchable":
                    issues = issues.Where(i => (i["subject"]!.GetValue<string>() + " " + (i["description"]?.GetValue<string>() ?? ""))
                        .Contains(values[0], StringComparison.OrdinalIgnoreCase));
                    break;
            }
        }
        issues = statusFilter switch
        {
            "o" => issues.Where(i => i["status"]!["is_closed"]?.GetValue<bool>() != true),
            "c" => issues.Where(i => i["status"]!["is_closed"]?.GetValue<bool>() == true),
            _ => issues,
        };
        var sort = plain.GetValueOrDefault("sort") ?? "id:desc";
        IOrderedEnumerable<JsonObject> ordered = sort.StartsWith("updated_on:desc", StringComparison.Ordinal)
            ? issues.OrderByDescending(i => i["updated_on"]!.GetValue<string>())
            : sort.StartsWith("priority:desc", StringComparison.Ordinal)
                ? issues.OrderByDescending(i => i["priority"]!["id"]!.GetValue<int>())
                : issues.OrderByDescending(Id);
        var list = ordered.ToList();
        var offset = int.Parse(plain.GetValueOrDefault("offset") ?? "0");
        var limit = int.Parse(plain.GetValueOrDefault("limit") ?? "25");
        return Json(new JsonObject
        {
            ["issues"] = new JsonArray(list.Skip(offset).Take(limit).Select(i => JsonNode.Parse(i.ToJsonString())).ToArray()),
            ["total_count"] = list.Count, ["offset"] = offset, ["limit"] = limit,
        });
    }

    private static int Id(JsonObject issue) => issue["id"]!.GetValue<int>();

    private static JsonObject Project(int id, string name, string identifier, int? parent)
    {
        var p = new JsonObject { ["id"] = id, ["name"] = name, ["identifier"] = identifier, ["status"] = 1 };
        if (parent is { } pid)
        {
            p["parent"] = new JsonObject { ["id"] = pid, ["name"] = "IT Support" };
        }
        return p;
    }

    private static List<(string Key, string Value)> Parse(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).Select(kv => (WebUtility.UrlDecode(kv[0]), kv.Length > 1 ? WebUtility.UrlDecode(kv[1]) : "")).ToList();

    private static HttpResponseMessage File(string name) =>
        new(HttpStatusCode.OK) { Content = new StringContent(Fixture.Read("redmine", name), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
}

/// <summary>The recorded answers, embedded in the assembly (PNMTD/Dev/Fixtures).</summary>
internal static class Fixture
{
    public static string Read(string set, string name)
    {
        var assembly = typeof(Fixture).Assembly;
        var wanted = $"fixtures/{set}/{name}";
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(n => n.Replace('\\', '/') == wanted)
            ?? throw new FileNotFoundException($"No fixture {wanted}");
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
