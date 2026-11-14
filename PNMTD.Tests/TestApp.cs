using PNMTD.Auth.Rights;
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
/// cookie scheme (what a signed-out client meets). <c>services</c> replaces services for the test.
/// </summary>
public sealed class TestApp(bool testSignIn = true, IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    : WebApplicationFactory<Program>
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
        if (services is not null)
        {
            // Replacements for the test, such as a simulated MikroTik router (docs/mikrotik-plan.md §14).
            builder.ConfigureTestServices(services);
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

    /// <summary>A client signed in as the Viewer's user, with exactly these rights ("Monitoring:Edit,Mail:Read", "admin", "").</summary>
    public HttpClient ClientWithRights(string rights)
    {
        var client = Client(Roles.Viewer);
        client.DefaultRequestHeaders.Add(TestSignIn.RightsHeader, rights);
        client.DefaultRequestHeaders.Add(TestSignIn.NameHeader, $"rights-{Guid.NewGuid():N}");
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
        Roles.Admin => Admin,
        Roles.User => User,
        _ => Viewer,
    };

    public static string NameOf(string role) => $"test-{role.ToLowerInvariant()}";

    /// <summary>For an AuthService outside an app: keys in memory, for the run.</summary>
    internal static readonly global::PNMTD.Auth.TotpSecrets TotpSecrets =
        new(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());

    internal static void Seed(PnmtdDbContext db)
    {
        var hash = Lib.Security.PasswordHasher.Hash(Password);
        foreach (var role in Roles.All)
        {
            var user = new Models.Db.UserEntity
            {
                Id = IdOf(role),
                Username = NameOf(role),
                NormalizedUsername = NameOf(role),
                DisplayName = $"Test {role}",
                PasswordHash = hash,
                IsAdmin = role == Roles.Admin,
                SecurityStamp = global::PNMTD.Auth.UserAccounts.NewStamp(),
                CreatedAt = Lib.Helper.Clock.UtcNow,
            };
            if (Roles.GroupOf(role) is { } group)
            {
                user.Groups.Add(new Models.Db.UserGroupEntity { UserId = user.Id, GroupId = group });
            }
            db.Users.Add(user);
        }
        db.SaveChanges();
    }
}

/// <summary>Signs a request in as the test user of the role its X-Test-Role header names; without it, signed out.</summary>
public sealed class TestSignIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Test";

    /// <summary>One of <see cref="Roles"/>: signs in as that seeded user, with its rights.</summary>
    public const string RoleHeader = "X-Test-Role";

    /// <summary>Exactly these rights instead ("Monitoring:Edit,Mail:Read", or "admin", or "" for none), as the role's user.</summary>
    public const string RightsHeader = "X-Test-Rights";

    /// <summary>Another name for the user (rate limits count per name).</summary>
    public const string NameHeader = "X-Test-Name";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RoleHeader, out var role) || string.IsNullOrEmpty(role))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var rights = Request.Headers.TryGetValue(RightsHeader, out var explicitRights) ? Roles.Parse(explicitRights.ToString()) : Roles.RightsOf(role!);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, TestUsers.IdOf(role!).ToString()),
                new Claim(ClaimTypes.Name, Request.Headers.TryGetValue(NameHeader, out var name) ? name.ToString() : TestUsers.NameOf(role!)), .. rights.Claims()],
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

/// <summary>
/// The tests' three kinds of user, named as the roles were before 1.4 (docs/audit-redmine-zabbix-plan.md §5.3): an admin,
/// a member of Operators (what User could do) and a member of Viewers (what Viewer could). PNMT itself has no roles.
/// </summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string User = "User";
    public const string Viewer = "Viewer";

    public static readonly string[] All = [Admin, User, Viewer];

    /// <summary>Operators' and Viewers' rights, as the update creates them.</summary>
    public static readonly IReadOnlyDictionary<Area, Level> Operators = new Dictionary<Area, Level>
    {
        [Area.Monitoring] = Level.Edit, [Area.Commands] = Level.Run, [Area.Mail] = Level.Read, [Area.Decisions] = Level.Decide,
        [Area.Locations] = Level.Edit, [Area.KnowledgeBase] = Level.Edit, [Area.Redmine] = Level.Link, [Area.Zabbix] = Level.Details,
    };

    public static readonly IReadOnlyDictionary<Area, Level> Viewers = new Dictionary<Area, Level>
    {
        [Area.Monitoring] = Level.View, [Area.Locations] = Level.View, [Area.KnowledgeBase] = Level.View, [Area.Zabbix] = Level.View,
    };

    public static Guid? GroupOf(string role) => role switch
    {
        User => Models.Db.GroupEntity.Operators,
        Viewer => Models.Db.GroupEntity.Viewers,
        _ => null,
    };

    public static UserRights RightsOf(string role) => role switch
    {
        Admin => new UserRights(true, new Dictionary<Area, Level>()),
        User => new UserRights(false, Operators),
        _ => new UserRights(false, Viewers),
    };

    public static bool IsAdmin(string role) => role == Admin;

    /// <summary>The groups a request gives a new user of this kind.</summary>
    public static List<Guid> GroupIds(string role) => GroupOf(role) is { } g ? [g] : [];

    /// <summary>What a user of the API amounts to: the flag, else Operators or Viewers.</summary>
    public static string Of(global::PNMTD.Api.V1.Contracts.UserItem user) =>
        user.IsAdmin ? Admin : user.Groups.Any(g => g.Id == Models.Db.GroupEntity.Operators) ? User : Viewer;

    public static string Of(global::PNMTD.Api.V1.Contracts.CurrentUser user) =>
        user.IsAdmin ? Admin : user.Rights.GetValueOrDefault(Area.Monitoring) == Level.Edit ? User : Viewer;

    public static string Of(Models.Db.UserEntity user, global::PNMTD.Data.PnmtdDbContext db) => user.IsAdmin ? Admin
        : db.UserGroups.Any(m => m.UserId == user.Id && m.GroupId == Models.Db.GroupEntity.Operators) ? User : Viewer;

    /// <summary>"Monitoring:Edit,Mail:Read", "admin", or "" (none).</summary>
    public static UserRights Parse(string value)
    {
        if (value == "admin")
        {
            return new UserRights(true, new Dictionary<Area, Level>());
        }
        var levels = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => RightsCatalog.TryParse(v.Trim(), out var a, out var l) ? (a, l) : throw new ArgumentException($"No such right: {v}"));
        return new UserRights(false, UserRights.Combine(levels));
    }
}
