using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.HttpOverrides;
using PNMTD.Api;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Helper;
using PNMTD.Hosting;
using PNMTD.Notifications;
using PNMTD.SensorApi;
using PNMTD.Services.Domain;
using PNMTD.Tasks;

namespace PNMTD;

public partial class Program
{
    public static int Main(string[] args)
    {
        // `pnmtd <command> …` (docker compose exec pnmtd pnmtd …): the command line, no web host.
        if (args.Length > 0 && Cli.CommandLine.IsCommand(args[0]))
        {
            return Cli.CommandLine.Run(args);
        }

        // The database is migrated by DatabaseMigrationService, the first hosted service.
        RunApi(args, out var appTask);
        appTask.Wait();
        return 0;
    }

    public static WebApplication RunApi(string[] args, out Task appTask)
    {
        var builder = WebApplication.CreateBuilder(args);

        var configJsonPath = Path.Combine(GlobalConfiguration.LinuxBasePath, "config.json");
        if (!Global.IsDevelopment && RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists(configJsonPath))
        {
            builder.Configuration.AddJsonFile(configJsonPath, true, true);
        }

        // PostgreSQL (§3.4): a context per request for controllers, the factory for background tasks.
        var connectionString = DatabaseSettings.ConnectionString(builder.Configuration);
        builder.Services.AddDbContextFactory<PnmtdDbContext>(o => PnmtdDbContext.Configure(o, connectionString));

        builder.Services.AddControllers();
        builder.Services.AddPnmtdApi();
        builder.Services.AddPnmtdAuth(builder.Configuration);
        builder.Services.AddDomainServices();
        // The v1 document only; written at build time to PNMTD.WebUI/src/api/openapi.json (§3.7).
        builder.Services.AddOpenApi("v1", o => o.ShouldInclude = d => d.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true);
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<NotificationService>();

        // The build writes the OpenAPI document by starting the host without a database (GetDocument.Insider).
        var documentBuild = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

        // First: the others start on a migrated database.
        if (!documentBuild)
        {
            builder.Services.AddHostedService<DatabaseMigrationService>();
        }

        // BackgroundTasks:Enabled=false: none of the monitoring tasks (the integration tests).
        if (!documentBuild && builder.Configuration.GetValue("BackgroundTasks:Enabled", true))
        {
            builder.Services.AddHostedService<NotificationTask>();
            builder.Services.AddHostedService<HeartbeatCheckTask>();
            builder.Services.AddHostedService<PingCheckTask>();
            builder.Services.AddHostedService<MailInboxCheckTask>();
            builder.Services.AddHostedService<MailProcessTask>();
            builder.Services.AddHostedService<TimespanConditionsCheckTask>();
            builder.Services.AddHostedService<UplinkCheckTask>();
            builder.Services.AddHostedService<DnsCheckTask>();
        }

        if (!string.IsNullOrWhiteSpace(builder.Configuration["Proxy"]))
        {
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.RequireHeaderSymmetry = false;
                options.ForwardLimit = null;
                if (builder.Configuration["Proxy"] == "docker")
                {
                    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("::ffff:172.16.0.0"), 12));
                }
                else
                {
                    // Example format: ::ffff:172.20.10.20
                    options.KnownProxies.Add(IPAddress.Parse(builder.Configuration["Proxy"]!));
                }
            });
        }

        if (!string.IsNullOrWhiteSpace(builder.Configuration["Identity"]))
        {
            ServerInfo.Identity = builder.Configuration["Identity"]!;
        }

        // Console only: in a container that is `docker compose logs`, rotated by the log driver.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddDebug();

        var app = builder.Build();

        Global.App = app;

        // The order matters (§3.5). First the client's real address and scheme. There is no HTTPS
        // redirect anywhere: TLS ends at the reverse proxy, and sensor URLs must never be redirected.
        app.UseForwardedHeaders();
        app.UseSecurityHeaders();          // not on /event and /read
        app.UseApiErrors();                // ProblemDetails, on /api only
        app.UseWebUiFiles();               // /pnmt-admin/** files, anonymous
        app.UseRouting();
        app.UsePlainNotFound();            // no endpoint: empty 404; routing's 405: empty, with Allow
        // The sensor API is anonymous and must not see the session cookie (a sliding renewal would
        // add a Set-Cookie header v0.12 never sent).
        app.UseWhen(c => !SensorPaths.IsSensorPath(c.Request.Path), b => b.UseAuthentication());
        app.UseAuthorization();            // deny by default (§3.6)

        // The container's health check: 200 while the database answers, 503 when it does not.
        // Nothing else, so it may be reachable from anywhere.
        app.MapGet("/healthz", async (PnmtdDbContext db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct)
                ? Results.Text("ok")
                : Results.Text("unavailable", statusCode: StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapControllers();              // /api/v1/**, /event/**, /read/**
        app.MapWebUi();                    // SPA deep links, /api 404s

        appTask = app.RunAsync();

        return app;
    }
}
