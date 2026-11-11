using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PNMTD.Data;

namespace PNMTD.Tests;

/// <summary>
/// The app in memory, on the run's PostgreSQL database (TestDefaults), in Development (no 5-second
/// rule). Sign-in arrives in phase 3; until then <see cref="TestSignIn"/> signs a request in with the
/// role in its <see cref="TestSignIn.RoleHeader"/> header. <c>testSignIn: false</c> keeps the real
/// cookie scheme (what a signed-out client meets).
/// </summary>
public sealed class TestApp(bool testSignIn = true, IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    private DbTestHelper? dbTestHelper;

    /// <summary>The seeded data and a context of its own, for the tests from before 1.0.</summary>
    internal DbTestHelper DbTestHelper => dbTestHelper ??= new DbTestHelper(TestDefaults.Configuration);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Global.IsDevelopment reads the variable itself.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        builder.UseEnvironment("Development");
        builder.UseSetting(WebHostDefaults.WebRootKey, TestDefaults.WebRoot);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }
        if (testSignIn)
        {
            builder.ConfigureTestServices(services => services
                .AddAuthentication(o =>
                {
                    o.DefaultScheme = TestSignIn.Scheme;
                    o.DefaultAuthenticateScheme = TestSignIn.Scheme;
                    o.DefaultChallengeScheme = TestSignIn.Scheme;
                    o.DefaultForbidScheme = TestSignIn.Scheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestSignIn>(TestSignIn.Scheme, null));
        }
    }

    /// <summary>A client signed in with <paramref name="role"/>, or signed out. Never follows redirects.</summary>
    public HttpClient Client(string? role = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (role is not null)
        {
            client.DefaultRequestHeaders.Add(TestSignIn.RoleHeader, role);
        }
        return client;
    }

    public PnmtdDbContext NewDb() => Services.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext();

    public void ClearDashboardCache() => Services.GetRequiredService<PNMTD.Services.Domain.DashboardCache>().Invalidate();
}

/// <summary>
/// One user per role, seeded once per run with <see cref="Password"/>: TestSignIn signs in as them,
/// and the cookie tests sign in with their passwords.
/// </summary>
public static class TestUsers
{
    public const string Password = "test-password-1";

    public static readonly Guid Admin = new("a0000000-0000-0000-0000-00000000000a");
    public static readonly Guid User = new("a0000000-0000-0000-0000-00000000000b");
    public static readonly Guid Viewer = new("a0000000-0000-0000-0000-00000000000c");

    public static Guid IdOf(string role) => role switch
    {
        global::PNMTD.Auth.Roles.Admin => Admin,
        global::PNMTD.Auth.Roles.User => User,
        _ => Viewer,
    };

    public static string NameOf(string role) => $"test-{role.ToLowerInvariant()}";

    internal static void Seed(PnmtdDbContext db)
    {
        var hash = Lib.Security.PasswordHasher.Hash(Password);
        foreach (var role in global::PNMTD.Auth.Roles.All)
        {
            db.Users.Add(new Models.Db.UserEntity
            {
                Id = IdOf(role),
                Username = NameOf(role),
                NormalizedUsername = NameOf(role),
                DisplayName = $"Test {role}",
                PasswordHash = hash,
                Role = role,
                SecurityStamp = global::PNMTD.Auth.UserAccounts.NewStamp(),
                CreatedAt = Lib.Helper.Clock.UtcNow,
            });
        }
        db.SaveChanges();
    }
}

/// <summary>Signs a request in as the test user of the role its X-Test-Role header names; without it, signed out.</summary>
public sealed class TestSignIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Test";

    public const string RoleHeader = "X-Test-Role";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RoleHeader, out var role) || string.IsNullOrEmpty(role))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, TestUsers.IdOf(role!).ToString()), new Claim(ClaimTypes.Name, TestUsers.NameOf(role!)), new Claim(ClaimTypes.Role, role!)],
            Scheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}

/// <summary>v1's JSON: camelCase, enums as strings.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        }
        return JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty body");
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, Options);

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient client, string url, object body) =>
        client.PutAsJsonAsync(url, body, Options);

    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response)
    {
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }
}
