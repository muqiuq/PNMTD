using System.Text;

namespace PNMTD.Contract.Tests.Harness;

/// <summary>
/// One request of the contract. <see cref="Path"/> may contain <c>{w:alias}</c> and <c>{r:alias}</c>,
/// replaced by the fixture sensor's write or read token. A body is sent as UTF-8 bytes, with a
/// Content-Type header only when <see cref="ContentType"/> is set.
/// </summary>
public sealed record ContractCase(
    string Id,
    string Method,
    string Path,
    string? Body = null,
    byte[]? RawBody = null,
    string? ContentType = null,
    string? Accept = null,
    int DelayBeforeMs = 0);

/// <summary>
/// The sensor API contract (docs/webui-migration-plan.md §3.3). Order matters: cases run one after
/// the other against a fresh copy of the fixture database, and the rate limit (5 s per sensor) and
/// the dedupe rule depend on what came before. A case that stores an event therefore uses a sensor
/// of its own, unless it tests exactly that interplay.
/// </summary>
public static class Cases
{
    /// <summary>Just over the 5-second rule, for sequences on one sensor.</summary>
    private const int AfterRateLimit = 5600;

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static ContractCase Get(string id, string path, string? accept = null, int delay = 0) =>
        new(id, "GET", path, Accept: accept, DelayBeforeMs: delay);

    private static ContractCase Post(string id, string path, string? body, string? contentType = "text/plain; charset=utf-8", int delay = 0) =>
        new(id, "POST", path, Body: body, ContentType: body is null ? null : contentType, DelayBeforeMs: delay);

    public static readonly IReadOnlyList<ContractCase> All = Build();

    private static List<ContractCase> Build()
    {
        var encMaxFirst = "[" + string.Join(",", Enumerable.Range(1, 66).Select(i => $"{{\"Name\":\"n{i:00}\",\"Code\":200,\"Message\":\"m{i}\"}}")) + "]";

        return
        [
            // --- paths outside the sensor API that must stay plain 404s
            Get("root", "/"),
            Get("event-bare", "/event"),
            Get("read-bare", "/read"),
            Get("event-token-only", "/event/{w:simple40}"),

            // --- GET /event: messages
            Get("get-basic", "/event/{w:simple01}/200/hello"),
            Get("get-no-message", "/event/{w:simple02}/200"),
            Get("get-trailing-slash", "/event/{w:simple03}/200/"),
            Get("get-percent-encoded", "/event/{w:simple04}/200/hello%20world%21"),
            Get("get-plus", "/event/{w:simple05}/200/a+b"),
            Get("get-unicode", "/event/{w:simple06}/200/Gr%C3%BCezi%20%E2%9C%93"),
            Get("get-special-chars", "/event/{w:simple07}/200/%22quoted%22%20%3C%3E%26%27"),
            Get("get-base64", "/event/{w:simple08}/200/$" + B64("Hello World")),
            Get("get-base64-unicode", "/event/{w:simple09}/200/$" + Uri.EscapeDataString(B64("Grüezi ✓"))),
            Get("get-base64-invalid", "/event/{w:simple10}/200/$not*base64"),
            Get("get-base64-urlsafe", "/event/{w:simple11}/200/$SGk-_w"),
            Get("get-base64-with-slash", "/event/{w:simple12}/200/$YT8/Pw=="),
            Get("get-base64-with-pct2f", "/event/{w:simple12}/200/$YT8%2FPw=="),
            Get("get-dollar-only", "/event/{w:simple13}/200/$"),
            Get("get-long-message", "/event/{w:simple14}/200/" + new string('x', 2000)),

            // --- GET /event: codes, tokens, routing
            Get("get-code-not-numeric", "/event/{w:simple15}/abc/x"),
            Get("get-code-overflow", "/event/{w:simple15}/99999999999/x"),
            Get("get-code-negative", "/event/{w:simple16}/-1/negative"),
            Get("get-code-failure", "/event/{w:simple17}/500/broken"),
            Get("get-unknown-token", "/event/unknown-token/200/x"),
            Get("get-read-token-as-write", "/event/{r:simple18}/200/x"),
            Get("get-disabled", "/event/{w:disabled}/200/x"),
            Get("get-ignored", "/event/{w:ignored}/200/x"),
            Get("get-duplicate-token", "/event/{w:dupA}/200/x"),
            Get("get-upper-case-path", "/EVENT/{w:simple19}/200/upper"),
            Get("get-query-ignored", "/event/{w:simple20}/200/withquery?foo=bar&message=other"),
            Get("get-too-many-segments", "/event/{w:simple21}/200/a/b"),
            Get("get-accept-json", "/event/{w:simple22}/200/acceptjson", accept: "application/json"),
            Get("get-accept-any", "/event/{w:simple23}/200/acceptany", accept: "*/*"),
            new("head-event", "HEAD", "/event/{w:simple24}/200/x"),
            new("options-event", "OPTIONS", "/event/{w:simple24}/200/x"),
            new("post-on-get-route", "POST", "/event/{w:simple24}/200/x", Body: "x", ContentType: "text/plain"),
            new("put-on-post-route", "PUT", "/event/{w:simple24}/200", Body: "x", ContentType: "text/plain"),
            new("delete-event", "DELETE", "/event/{w:simple24}/200"),

            // --- rate limit (5 s per sensor, Production only)
            Get("rate-first", "/event/{w:rate}/200/a"),
            Get("rate-second", "/event/{w:rate}/200/b"),
            Post("rate-post-first", "/event/{w:rate2}/200", "a"),
            Post("rate-post-second", "/event/{w:rate2}/200", "b"),

            // --- dedupe: the third identical submission updates the second instead of adding a row
            Get("dedupe-1", "/event/{w:dedupe}/200/same"),
            Get("dedupe-2", "/event/{w:dedupe}/200/same", delay: AfterRateLimit),
            Get("dedupe-3", "/event/{w:dedupe}/200/same", delay: AfterRateLimit),
            Post("dedupe-post-1", "/event/{w:dedupePost}/200", "same"),
            Post("dedupe-post-2", "/event/{w:dedupePost}/200", "same", delay: AfterRateLimit),
            Post("dedupe-post-3", "/event/{w:dedupePost}/200", "same", delay: AfterRateLimit),

            // --- value checks
            Get("vc-match", "/event/{w:vc1}/200/OK%20all%20good"),
            Get("vc-no-match", "/event/{w:vc2}/200/disk%20full"),
            Get("vc-no-message", "/event/{w:vc3}/200"),
            Post("vc-post-match", "/event/{w:vcpost}/500", "OK via post"),
            Get("vc-invalid-regex", "/event/{w:vcbad}/200/x"),
            Get("vc-no-parameters", "/event/{w:vcnull}/200/x"),
            Get("hbvc-match", "/event/{w:hbvc}/200/12345"),
            Get("hb-basic", "/event/{w:hb}/200/alive"),

            // --- POST /event
            Post("post-text", "/event/{w:simple25}/200", "hello post"),
            Post("post-empty", "/event/{w:simple26}/200", ""),
            new("post-no-body", "POST", "/event/{w:simple27}/200"),
            Post("post-whitespace", "/event/{w:simple28}/200", "  padded \r\n\t"),
            Post("post-form", "/event/{w:simple29}/200", "a=b&c=d", "application/x-www-form-urlencoded"),
            Post("post-json-content-type", "/event/{w:simple30}/200", "{\"x\":1}", "application/json"),
            Post("post-no-content-type", "/event/{w:simple31}/200", "no content type", contentType: null),
            new("post-bom", "POST", "/event/{w:simple32}/200", RawBody: [0xEF, 0xBB, 0xBF, (byte)'b', (byte)'o', (byte)'m'], ContentType: "text/plain"),
            Post("post-base64", "/event/{w:simple33}/200", "$" + B64("Hello from base64")),
            Post("post-base64-padded", "/event/{w:simple34}/200", "  $" + B64(" keeps inner spaces ") + "  "),
            Post("post-base64-invalid", "/event/{w:simple35}/200", "$***"),
            Post("post-unicode", "/event/{w:simple36}/200", "Grüezi ✓ \"<>&'"),
            Post("post-multiline", "/event/{w:simple37}/200", "line1\nline2\r\nline3"),
            Post("post-code-not-numeric", "/event/{w:simple38}/abc", "x"),
            Post("post-unknown-token", "/event/unknown-token/200", "x"),
            Post("post-disabled", "/event/{w:disabled}/200", "x"),
            Post("post-duplicate-token", "/event/{w:dupA}/200", "x"),
            Post("post-trailing-slash", "/event/{w:simple39}/200/", "trailing"),
            Post("post-upper-case-path", "/Event/{w:simple40}/201", "upper"),

            // --- ENCAPSULADED: one sibling sensor per Name, the parent gets no event
            Post("enc-valid", "/event/{w:enc1}/999", "[{\"Name\":\"a\",\"Code\":200,\"Message\":\"ok\"},{\"Name\":\"b\",\"Code\":500,\"Message\":\"fail\"}]", "application/json"),
            Post("enc-reuse-sibling", "/event/{w:enc1}/0", "[{\"Name\":\"a\",\"Code\":201,\"Message\":\"again\"}]", "application/json"),
            Post("enc-duplicate-names", "/event/{w:enc2}/200", "[{\"Name\":\"x\",\"Code\":200,\"Message\":\"1\"},{\"Name\":\"x\",\"Code\":500,\"Message\":\"2\"}]", "application/json"),
            Post("enc-no-message", "/event/{w:enc2}/200", "[{\"Name\":\"y\",\"Code\":200}]", "application/json"),
            Post("enc-base64", "/event/{w:enc3}/200", "$" + B64("[{\"Name\":\"z\",\"Code\":200,\"Message\":\"b64\"}]")),
            Post("enc-lower-case-names", "/event/{w:enc4}/200", "[{\"name\":\"a\",\"code\":200,\"message\":\"x\"}]", "application/json"),
            Post("enc-invalid-json", "/event/{w:enc4}/200", "not json"),
            Post("enc-empty-body", "/event/{w:enc4}/200", ""),
            Post("enc-null", "/event/{w:enc4}/200", "null"),
            Post("enc-empty-array", "/event/{w:enc4}/200", "[]"),
            Post("enc-code-as-string", "/event/{w:enc5}/200", "[{\"Name\":\"s\",\"Code\":\"200\",\"Message\":\"x\"}]"),
            Get("enc-get", "/event/{w:enc5}/200/via-get"),
            Post("enc-max-first", "/event/{w:encmax}/200", encMaxFirst, "application/json"),
            Post("enc-max-exceeded", "/event/{w:encmax}/200", "[{\"Name\":\"one-too-many\",\"Code\":200,\"Message\":\"x\"}]", "application/json"),
            Post("enc-max-existing-name", "/event/{w:encmax}/200", "[{\"Name\":\"n01\",\"Code\":500,\"Message\":\"existing\"}]", "application/json"),

            // --- GET /read
            Post("read-setup-msg", "/event/{w:readmsg}/200", "Grüezi \"quoted\" <tag> & 'apostrophe' +plus `tick`\nsecond line"),
            Get("read-json", "/read/{r:readmsg}/json"),
            Get("read-json-upper", "/read/{r:readmsg}/JSON"),
            Get("read-default", "/read/{r:readmsg}"),
            Get("read-default-trailing-slash", "/read/{r:readmsg}/"),
            Get("read-property", "/read/{r:readmsg}/property"),
            Get("read-raw", "/read/{r:readmsg}/raw"),
            Get("read-unknown-option", "/read/{r:readmsg}/xml"),
            Get("read-accept-json", "/read/{r:readmsg}/raw", accept: "application/json"),
            Get("read-accept-json-default", "/read/{r:readmsg}/json", accept: "application/json"),
            Get("read-accept-any", "/read/{r:readmsg}/raw", accept: "*/*"),
            Get("read-accept-text", "/read/{r:readmsg}/raw", accept: "text/plain"),
            Get("read-accept-xml", "/read/{r:readmsg}/raw", accept: "application/xml"),
            Get("read-upper-case-path", "/READ/{r:readmsg}/raw"),
            Get("read-query-ignored", "/read/{r:readmsg}/raw?option=json"),
            Get("read-too-many-segments", "/read/{r:readmsg}/raw/extra"),
            new("head-read", "HEAD", "/read/{r:readmsg}/raw"),
            new("post-read", "POST", "/read/{r:readmsg}/raw", Body: "x", ContentType: "text/plain"),
            Get("read-none-json", "/read/{r:readnone}/json"),
            Get("read-none-property", "/read/{r:readnone}/property"),
            Get("read-none-raw", "/read/{r:readnone}/raw"),
            Get("read-setup-fail", "/event/{w:readfail}/503/service%20down"),
            Get("read-fail-json", "/read/{r:readfail}/json"),
            Get("read-fail-raw", "/read/{r:readfail}/raw"),
            Get("read-fail-property", "/read/{r:readfail}/property"),
            Get("read-setup-null-message", "/event/{w:readnull}/200"),
            Get("read-null-message-json", "/read/{r:readnull}/json"),
            Get("read-null-message-raw", "/read/{r:readnull}/raw"),
            Post("read-setup-empty-message", "/event/{w:readempty}/200", ""),
            Get("read-empty-message-property", "/read/{r:readempty}/property"),
            Get("read-unknown-token", "/read/unknown-token/json"),
            Get("read-write-token", "/read/{w:readmsg}/json"),
            Get("read-disabled", "/read/{r:readdisabled}/json"),
            Get("read-ignored-setup", "/event/{w:readignored}/200/ignored%20but%20readable"),
            Get("read-ignored", "/read/{r:readignored}/raw"),
            Get("read-duplicate-token", "/read/{r:dupA}/json"),

            // --- timespan sensors read only their 280/281/480/481 events
            Get("one-setup-plain", "/event/{w:oneA}/200/plain"),
            Get("one-read-plain", "/read/{r:oneA}/json"),
            Get("one-setup-280", "/event/{w:oneB}/280/within"),
            Get("one-read-280", "/read/{r:oneB}/json"),
            Get("all-setup-481", "/event/{w:allA}/481/missed"),
            Get("all-read-481", "/read/{r:allA}/raw"),
            Get("all-read-none", "/read/{r:allB}/property"),
        ];
    }
}
