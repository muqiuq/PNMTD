using System.Net;
using System.Text.Json;
using PNMTD.Auth;

namespace PNMTD.Tests.Hosting;

/// <summary>
/// The admin UI under /pnmt-admin, the plain 404s, and the server protecting itself
/// (docs/webui-migration-plan.md §3.5, §3.6, §8.2). Signed out means the real cookie scheme.
/// </summary>
[TestClass]
public sealed class HostingTests
{
    private static TestApp signedOut = null!;
    private static TestApp signedIn = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        signedOut = new TestApp(testSignIn: false);
        signedIn = new TestApp();
    }

    [ClassCleanup]
    public static void Stop()
    {
        signedOut.Dispose();
        signedIn.Dispose();
    }

    [TestMethod]
    public async Task UiBaseWithoutSlashRedirectsPermanently()
    {
        using var response = await signedOut.Client().GetAsync("/pnmt-admin?x=1");
        Assert.AreEqual(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.AreEqual("/pnmt-admin/?x=1", response.Headers.Location?.OriginalString);
    }

    [TestMethod]
    [DataRow("/pnmt-admin/")]
    [DataRow("/pnmt-admin/hosts/3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [DataRow("/pnmt-admin/login")]
    public async Task DeepLinksGetIndexHtmlRevalidated(string path)
    {
        using var response = await signedOut.Client().GetAsync(path);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(TestDefaults.IndexHtml, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("no-cache", response.Headers.CacheControl?.ToString());
        StringAssert.StartsWith(Header(response, "Content-Security-Policy"), "default-src 'self'");
        Assert.AreEqual("DENY", Header(response, "X-Frame-Options"));
    }

    [TestMethod]
    public async Task HashedAssetsAreImmutable()
    {
        using var response = await signedOut.Client().GetAsync("/pnmt-admin/assets/app-3f9a.js");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
    }

    [TestMethod]
    public async Task MissingAssetIsA404NotTheUi()
    {
        using var response = await signedOut.Client().GetAsync("/pnmt-admin/assets/missing.js");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual("", await response.Content.ReadAsStringAsync());
    }

    /// <summary>The root and every route of the old management API: an empty 404, headers as in v0.12.</summary>
    [TestMethod]
    [DataRow("/")]
    [DataRow("/foo")]
    [DataRow("/host")]
    [DataRow("/hosts")]
    [DataRow("/sensor")]
    [DataRow("/events/last")]
    [DataRow("/events/lasterrors")]
    [DataRow("/notificationrule")]
    [DataRow("/dnszone")]
    [DataRow("/mailinput")]
    [DataRow("/maillog")]
    [DataRow("/keyvalue")]
    [DataRow("/info")]
    [DataRow("/state")]
    [DataRow("/swagger")]
    [DataRow("/swagger/index.html")]
    [DataRow("/trust/create-web-app-token")]
    [DataRow("/openapi/v1.json")]
    public async Task RootAndRemovedRoutesAreEmpty404s(string path)
    {
        foreach (var app in new[] { signedOut, signedIn })
        {
            using var response = await app.Client(app == signedIn ? Roles.Admin : null).GetAsync(path);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, path);
            Assert.AreEqual("", await response.Content.ReadAsStringAsync(), path);
            Assert.IsFalse(response.Headers.Contains("Content-Security-Policy"), path);
        }
    }

    [TestMethod]
    public async Task UnknownApiPathIs401SignedOutAnd404SignedIn()
    {
        using var anonymous = await signedOut.Client().GetAsync("/api/v1/foo");
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.AreEqual("unauthorized", (await anonymous.ProblemAsync()).GetProperty("code").GetString());

        using var viewer = await signedIn.Client(Roles.Viewer).GetAsync("/api/v1/foo/bar");
        Assert.AreEqual(HttpStatusCode.NotFound, viewer.StatusCode);
        Assert.AreEqual("not_found", (await viewer.ProblemAsync()).GetProperty("code").GetString());

        using var outsideV1 = await signedIn.Client(Roles.Viewer).GetAsync("/api/v2/hosts");
        Assert.AreEqual(HttpStatusCode.NotFound, outsideV1.StatusCode);
    }

    /// <summary>The cookie scheme never redirects to a login page and never hands out a cookie unasked.</summary>
    [TestMethod]
    public async Task SignedOutApiCallsGet401WithoutRedirect()
    {
        foreach (var (method, path) in new[] { ("GET", "/api/v1/hosts"), ("GET", "/api/v1/dashboard"), ("POST", "/api/v1/hosts"), ("DELETE", $"/api/v1/hosts/{Guid.NewGuid()}") })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "POST")
            {
                request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            }
            using var response = await signedOut.Client().SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {path}");
            Assert.IsNull(response.Headers.Location, $"{method} {path}");
            Assert.IsFalse(response.Headers.Contains("Set-Cookie"), $"{method} {path}");
            Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString(), $"{method} {path}");
        }
    }

    [TestMethod]
    public async Task ViewerGets403ForAWrite()
    {
        using var response = await signedIn.Client(Roles.Viewer).PostJsonAsync("/api/v1/hosts", new { name = "viewer-host" });
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.AreEqual(("missing_right", "Monitoring", "Edit"), (problem.GetProperty("code").GetString(), problem.GetProperty("area").GetString(),
            problem.GetProperty("level").GetString()));
        Assert.AreEqual("You need Monitoring: Edit.", problem.GetProperty("detail").GetString());
    }

    /// <summary>The sensor API keeps v0.12's headers; the API gets the security headers.</summary>
    [TestMethod]
    public async Task SecurityHeadersOnTheApiNotOnTheSensorApi()
    {
        using var sensor = await signedOut.Client().GetAsync("/read/no-such-token/json");
        Assert.AreEqual(HttpStatusCode.NotFound, sensor.StatusCode);
        foreach (var header in new[] { "Content-Security-Policy", "X-Content-Type-Options", "X-Frame-Options", "Referrer-Policy", "Cache-Control" })
        {
            Assert.IsFalse(sensor.Headers.Contains(header), header);
        }

        using var api = await signedIn.Client(Roles.Viewer).GetAsync("/api/v1/system/info");
        Assert.AreEqual("default-src 'none'; frame-ancestors 'none'", Header(api, "Content-Security-Policy"));
        Assert.AreEqual("nosniff", Header(api, "X-Content-Type-Options"));
        Assert.AreEqual("no-referrer", Header(api, "Referrer-Policy"));
    }

    /// <summary>
    /// Deny by default must not turn v0.12's empty 405 (with Allow) into a 401 for a client without
    /// a session (§3.3); the contract replay checks the same against the image.
    /// </summary>
    [TestMethod]
    public async Task WrongMethodOnASensorRouteIsAnEmpty405()
    {
        using var response = await signedOut.Client().PostAsync("/event/token/200/message", new StringContent("x"));
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.AreEqual("GET", string.Join(",", response.Content.Headers.Allow));
        Assert.AreEqual("", await response.Content.ReadAsStringAsync());

        using var head = await signedOut.Client().SendAsync(new HttpRequestMessage(HttpMethod.Head, "/read/token/json"));
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, head.StatusCode);
    }

    [TestMethod]
    public async Task UnknownTokenIsTheSensorApis404()
    {
        using var response = await signedOut.Client().GetAsync("/EVENT/no-such-token/200/hello");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.IsFalse(body.TryGetProperty("code", out _), "v0.12's ProblemDetails, without v1's code");
    }

    [TestMethod]
    public async Task HealthzIsAnonymous()
    {
        using var response = await signedOut.Client().GetAsync("/healthz");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Signed out, system/info tells the login screen the identity and nothing else.</summary>
    [TestMethod]
    public async Task SystemInfoSignedOutHasOnlyTheIdentity()
    {
        using var response = await signedOut.Client().GetAsync("/api/v1/system/info");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        CollectionAssert.AreEqual(new[] { "identity" }, body.EnumerateObject().Select(p => p.Name).ToArray());
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}
