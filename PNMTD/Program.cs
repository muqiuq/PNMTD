using PNMTD.Redmine;
using PNMTD.Zabbix;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.HttpOverrides;
using PNMTD.Ai;
using PNMTD.Api;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Helper;
using PNMTD.Hosting;
using PNMTD.Mikrotik;
using PNMTD.Notifications;
using PNMTD.SensorApi;
using PNMTD.Services.Domain;
using PNMTD.Tasks;
using PNMTD.Udp;
using PNMTD.WireGuard;
using PNMTD.Agents;
using PNMTD.Files;

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
        builder.Services.AddOpenApi("v1", o =>
        {
            o.ShouldInclude = d => d.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true;
            // Query parameters in camelCase, as the JSON (?page=2&pageSize=50&hostId=…). Binding
            // ignores case, so this only names them in the document the UI's types come from.
            o.AddOperationTransformer((operation, _, _) =>
            {
                foreach (var parameter in operation.Parameters?.OfType<Microsoft.OpenApi.OpenApiParameter>() ?? [])
                {
                    if (parameter.In == Microsoft.OpenApi.ParameterLocation.Query && parameter.Name is { Length: > 0 } name)
                    {
                        parameter.Name = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(name);
                    }
                }
                return Task.CompletedTask;
            });
            // A file upload (the firmware) is multipart/form-data, which the document does not infer
            // without the Consumes constraint that ApiSetup turns off.
            o.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.ParameterDescriptions.Any(p => p.Type == typeof(IFormFile))
                    && operation.RequestBody is Microsoft.OpenApi.OpenApiRequestBody { Content: { } content }
                    && content.Remove("application/x-www-form-urlencoded", out var media))
                {
                    content["multipart/form-data"] = media;
                }
                return Task.CompletedTask;
            });
        });
        builder.Services.AddHttpClient();
        builder.Services.AddNotifications();
        // PNMT's own conditions as sensors, and the uplink (docs/ai-operations-plan.md §4, §7).
        builder.Services.AddSingleton<PNMTD.Services.Events.SystemSensors>();
        builder.Services.AddHttpClient(PNMTD.UplinkCheck.UplinkMonitor.HttpClientName);
        builder.Services.AddSingleton<PNMTD.UplinkCheck.UplinkMonitor>();
        builder.Services.AddAi(builder.Configuration);
        builder.Services.AddUdpNodes(builder.Configuration);
        builder.Services.AddMikrotik(builder.Configuration);
        builder.Services.AddWireGuard(builder.Configuration);
        builder.Services.AddAgents(builder.Configuration);
        builder.Services.AddFiles(builder.Configuration);
        builder.Services.AddAudit(builder.Configuration);
        builder.Services.AddRedmine(builder.Configuration);
        builder.Services.AddZabbix(builder.Configuration);

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
            builder.Services.AddHostedService<PNMTD.Services.Events.SystemSensorStartup>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<PNMTD.Ai.AiWorker>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<PNMTD.Decisions.DecisionMaintenanceTask>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<PNMTD.Ai.Analysis.SituationScheduler>());
            builder.Services.AddHostedService<NotificationTask>();
            builder.Services.AddHostedService<HeartbeatCheckTask>();
            builder.Services.AddHostedService<PingCheckTask>();
            builder.Services.AddHostedService<MailInboxCheckTask>();
            builder.Services.AddHostedService<MailProcessTask>();
            builder.Services.AddHostedService<TimespanConditionsCheckTask>();
            builder.Services.AddHostedService<UplinkCheckTask>();
            builder.Services.AddHostedService<DnsCheckTask>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<UdpMaintenanceTask>());
            // MikroTik routers (docs/mikrotik-plan.md §5.2, §5.4, §6.3); Mikrotik:Enabled=false pauses the poller and the version check.
            builder.Services.AddHostedService(sp => sp.GetRequiredService<RouterPoller>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<VersionCatalog>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<AdvisoryFeed>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupRetentionTask>());
            // Servers with an agent (docs/windows-sensor-plan.md §8.4): expired tokens and retention.
            builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentMaintenanceTask>());
            // Uploaded files (docs/locations-knowledge-plan.md §5.5): the daily cleanup, and the check on the File storage sensor.
            builder.Services.AddHostedService(sp => sp.GetRequiredService<FileCleanupTask>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<FileCheckTask>());
            // The audit log's retention (docs/audit-redmine-zabbix-plan.md §3.8).
            builder.Services.AddHostedService(sp => sp.GetRequiredService<PNMTD.Audit.AuditRetentionTask>());
            // Redmine's copy of linked tickets (docs/audit-redmine-zabbix-plan.md §6.7).
            builder.Services.AddHostedService(sp => sp.GetRequiredService<PNMTD.Redmine.RedmineSync>());
            // Zabbix's open problems and its hosts' mirror (docs/audit-redmine-zabbix-plan.md §7.3).
            builder.Services.AddHostedService(sp => sp.GetRequiredService<PNMTD.Zabbix.ZabbixPoller>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<PNMTD.Zabbix.ZabbixHostSync>());
        }

        // UDP nodes (docs/udp-iot-plan.md §4.4): on unless Udp:Enabled=false, not tied to the
        // monitoring tasks, so the integration tests can run it on a port of their own. The same for
        // MikroTik jobs (docs/mikrotik-plan.md §9.3): an update an Admin started runs in any case.
        if (!documentBuild)
        {
            builder.Services.AddHostedService<UdpListener>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<JobRunner>());
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
        app.UseAgentWebSockets();          // /agent/v1/ws only (docs/windows-sensor-plan.md §3.5)
        app.UseSecurityHeaders();          // /pnmt-admin and /api only
        app.UseApiErrors();                // ProblemDetails, on /api only
        app.UseWebUiFiles();               // /pnmt-admin/** files, anonymous
        app.UseRouting();
        app.UsePlainNotFound();            // no endpoint: empty 404; routing's 405: empty, with Allow
        app.UseRateLimiter();              // the login endpoints only
        // The sensor API is anonymous and must not see the session cookie (a sliding renewal would
        // add a Set-Cookie header v0.12 never sent).
        // Nor do the agents' paths, which authenticate by token or key (docs/windows-sensor-plan.md §9.1).
        app.UseWhen(c => !SensorPaths.IsSensorPath(c.Request.Path) && !PNMTD.Agents.AgentPaths.IsAgentPath(c.Request.Path), b => b.UseAuthentication());
        app.UseAuthorization();            // deny by default (§3.6)
        app.UseSessionGates();   // a temporary password must be changed first

        // The container's health check: 200 while the database answers, 503 when it does not.
        // Nothing else, so it may be reachable from anywhere.
        app.MapGet("/healthz", async (PnmtdDbContext db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct)
                ? Results.Text("ok")
                : Results.Text("unavailable", statusCode: StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapControllers();              // /api/v1/**, /event/**, /read/**
        app.MapAgentEndpoints();           // /agent/v1/join, /agent/v1/ws
        app.MapWebUi();                    // SPA deep links, /api 404s

        appTask = app.RunAsync();

        return app;
    }
}
