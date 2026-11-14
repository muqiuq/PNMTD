using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace PNMTD.Tests.Zabbix;

/// <summary>
/// A Zabbix for the tests (docs/audit-redmine-zabbix-plan.md §12), answering as Zabbix 6.0, 7.0 or 8.0 did in Z0 (one set of
/// fixtures each): the token only where the version takes it (6.0 the request's auth field, 7.0 either, 8.0 the Bearer
/// header), apiinfo.version only without it, host groups under the key the version uses, the role's allow list, and the
/// filters PNMT sends (groupids, hostids, eventids, triggerids, search). It records every request; it can be down.
/// </summary>
public sealed class FakeZabbix : HttpMessageHandler
{
    public const string Url = "http://zabbix.test";
    public const string Token = "test-zabbix-token-0123456789abcdef";

    public static readonly IReadOnlyDictionary<string, string?> Settings = new Dictionary<string, string?>
    {
        ["Zabbix:Url"] = Url,
        ["Zabbix:ApiToken"] = Token,
        ["Zabbix:HostGroups"] = "PNMT Servers",
        ["Zabbix:PublicUrl"] = "https://zabbix.example.com",
    };

    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Zabbix", "Fixtures");

    private static readonly HashSet<string> Allowed = ["hostgroup.get", "host.get", "problem.get", "trigger.get", "item.get"];

    public sealed record Call(string Method, JsonObject Params, string? AuthField, string? Bearer);

    public List<Call> Calls { get; } = [];

    public bool Down { get; set; }

    /// <summary>As if the token were deleted or expired.</summary>
    public bool RefuseToken { get; set; }

    /// <summary>"60", "70" or "80".</summary>
    public string Version { get; private set; } = "70";

    public string VersionText { get; private set; } = "";

    public JsonArray Groups { get; private set; } = [];

    public JsonArray Hosts { get; private set; } = [];

    public JsonObject Detail { get; private set; } = [];

    public JsonArray Problems { get; private set; } = [];

    public JsonArray Triggers { get; private set; } = [];

    public JsonArray Items { get; private set; } = [];

    public FakeZabbix() => Load("70");

    /// <summary>A version's fixtures, afresh.</summary>
    public void Load(string version)
    {
        Version = version;
        JsonNode Read(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, $"zabbix-{version}", name)))!;
        VersionText = Read("apiinfo_version.json")["result"]!.GetValue<string>();
        Groups = Read("hostgroup_get.json")["result"]!.AsArray();
        Hosts = Read("host_get_mirror.json")["result"]!.AsArray();
        Detail = Read("host_get_detail.json")["result"]![0]!.AsObject();
        Problems = Read("problem_get.json")["result"]!.AsArray();
        Triggers = Read("trigger_get.json")["result"]!.AsArray();
        Items = Read("item_get.json")["result"]!.AsArray();
        lock (Calls)
        {
            Calls.Clear();
        }
    }

    public void Install(IServiceCollection services) =>
        services.AddHttpClient(global::PNMTD.Zabbix.ZabbixClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => this);

    /// <summary>A host's id by its technical name, in this version's fixtures.</summary>
    public string HostId(string technicalName) => Hosts.Single(h => h!["host"]!.GetValue<string>() == technicalName)!["hostid"]!.GetValue<string>();

    public string GroupId(string name) => Groups.Single(g => g!["name"]!.GetValue<string>() == name)!["groupid"]!.GetValue<string>();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
        {
            throw new HttpRequestException("Connection refused (zabbix.test:80)");
        }
        if (request.RequestUri!.AbsolutePath != "/api_jsonrpc.php" || request.Method != HttpMethod.Post)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        var method = body["method"]!.GetValue<string>();
        var p = body["params"] as JsonObject ?? [];
        var auth = body["auth"]?.GetValue<string>();
        var bearer = request.Headers.Authorization is { Scheme: "Bearer" } h ? h.Parameter : null;
        lock (Calls)
        {
            Calls.Add(new Call(method, p, auth, bearer));
        }
        if (Version == "80" && body.ContainsKey("auth"))
        {
            return Error(-32600, "Invalid request.", "Invalid parameter \"/\": unexpected parameter \"auth\".");
        }
        if (method == "apiinfo.version")
        {
            if (Version != "80" && (auth is not null || bearer is not null))
            {
                return Error(-32602, "Invalid params.", Version == "60"
                    ? "The \"apiinfo.version\" method must be called without the \"auth\" parameter."
                    : "The \"apiinfo.version\" method must be called without authorization header.");
            }
            return Result(JsonValue.Create(VersionText));
        }
        var token = Version switch { "60" => auth, "70" => bearer ?? auth, _ => bearer };
        if (token != Token || RefuseToken)
        {
            return Error(-32602, "Invalid params.", "Not authorized.");
        }
        if (!Allowed.Contains(method))
        {
            return Error(Version == "60" ? -32602 : -32500, Version == "60" ? "Invalid params." : "Application error.", $"No permissions to call \"{method}\".");
        }
        return Result(method switch
        {
            "hostgroup.get" => HostGroupGet(p),
            "host.get" => HostGet(p),
            "problem.get" => ProblemGet(p),
            "trigger.get" => new JsonArray(Triggers.Where(t => In(p, "triggerids", t!["triggerid"])).Select(Clone).ToArray()),
            _ => ItemGet(p),
        });
    }

    private JsonArray HostGroupGet(JsonObject p)
    {
        var names = (p["filter"]?["name"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToHashSet();
        return new JsonArray(Groups.Where(g => names is null || names.Contains(g!["name"]!.GetValue<string>())).Select(Clone).ToArray());
    }

    private static IEnumerable<string> GroupIdsOf(JsonNode host) =>
        ((host["groups"] ?? host["hostgroups"]) as JsonArray ?? []).Select(g => g!["groupid"]!.GetValue<string>());

    private JsonArray HostGet(JsonObject p)
    {
        var detail = p.ContainsKey("selectParentTemplates");
        var hosts = Hosts.Where(h => In(p, "hostids", h!["hostid"]) && InGroups(p, h!));
        // Which key the version answers host groups under (Z0): 6.0 knows only selectGroups, 8.0 only selectHostGroups.
        var key = p.ContainsKey("selectHostGroups") && Version != "60" ? "hostgroups"
            : p.ContainsKey("selectGroups") && Version != "80" ? "groups" : null;
        return new JsonArray(hosts.Select(h =>
        {
            var source = detail && h!["hostid"]!.GetValue<string>() == Detail["hostid"]!.GetValue<string>() ? Detail : h!;
            var copy = Clone(source)!.AsObject();
            var groups = Clone(source["groups"] ?? source["hostgroups"]);
            copy.Remove("groups");
            copy.Remove("hostgroups");
            if (key is not null)
            {
                copy[key] = groups;
            }
            if (!p.ContainsKey("selectInventory"))
            {
                copy.Remove("inventory");
            }
            return (JsonNode)copy;
        }).ToArray());
    }

    private bool InGroups(JsonObject p, JsonNode host) =>
        p["groupids"] is not JsonArray wanted || GroupIdsOf(host).Any(g => wanted.Any(w => w!.GetValue<string>() == g));

    private JsonArray ProblemGet(JsonObject p)
    {
        var problems = Problems.Where(x => In(p, "eventids", x!["eventid"])).Where(x =>
        {
            if (p["groupids"] is not JsonArray)
            {
                return true;
            }
            var trigger = Triggers.FirstOrDefault(t => t!["triggerid"]!.GetValue<string>() == x!["objectid"]!.GetValue<string>());
            var hostIds = (trigger?["hosts"] as JsonArray ?? []).Select(h => h!["hostid"]!.GetValue<string>()).ToHashSet();
            return Hosts.Any(h => hostIds.Contains(h!["hostid"]!.GetValue<string>()) && InGroups(p, h!));
        });
        var extend = p["selectAcknowledges"] is JsonValue v && v.GetValue<string>() == "extend";
        return new JsonArray(problems.Select(x =>
        {
            var copy = Clone(x)!.AsObject();
            if (!extend)
            {
                copy["acknowledges"] = new JsonArray((x!["acknowledges"] as JsonArray ?? [])
                    .Select(a => (JsonNode)new JsonObject { ["acknowledgeid"] = a!["acknowledgeid"]!.GetValue<string>() }).ToArray());
            }
            return (JsonNode)copy;
        }).ToArray());
    }

    private JsonArray ItemGet(JsonObject p)
    {
        var search = p["search"]?["name"]?.GetValue<string>();
        // The items were recorded for web01 only.
        var hostIds = (p["hostids"] as JsonArray ?? []).Select(h => h!.GetValue<string>()).ToList();
        var web01 = Detail["hostid"]!.GetValue<string>();
        return new JsonArray(Items.Where(_ => hostIds.Count == 0 || hostIds.Contains(web01))
            .Where(i => search is null || i!["name"]!.GetValue<string>().Contains(search, StringComparison.OrdinalIgnoreCase)).Select(Clone).ToArray());
    }

    private static bool In(JsonObject p, string name, JsonNode? id) =>
        p[name] is not JsonArray wanted || wanted.Any(w => w!.GetValue<string>() == id!.GetValue<string>());

    private static JsonNode? Clone(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());

    private static HttpResponseMessage Result(JsonNode? result) =>
        Json(new JsonObject { ["jsonrpc"] = "2.0", ["result"] = result, ["id"] = 1 });

    private static HttpResponseMessage Error(int code, string message, string data) =>
        Json(new JsonObject { ["jsonrpc"] = "2.0", ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["data"] = data }, ["id"] = 1 });

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
}
