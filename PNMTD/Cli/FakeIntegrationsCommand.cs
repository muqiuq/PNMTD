using PNMTD.Dev;

namespace PNMTD.Cli;

/// <summary>
/// <c>pnmtd dev fake-integrations</c> (docs/audit-redmine-zabbix-plan.md §11, F): a Redmine and a Zabbix answering from the
/// answers R0 and Z0 recorded, for a development stack and the browser tests, as <c>agent simulate</c> is an agent. Redmine
/// is at <c>/redmine</c>, Zabbix at <c>/zabbix</c>; their keys are the tests' ones, printed at the start.
/// </summary>
internal static class FakeIntegrationsCommand
{
    public const string Usage = """
          dev fake-integrations [--port 7390] [--zabbix 60|70|80] [--tls-port 7391]
                                                Development only: a Redmine and a Zabbix (6.0, 7.0 or 8.0)
                                                answering with recorded tickets, hosts and problems; with
                                                --tls-port, TLS endpoints for SSL certificate sensors
        """;

    public static int Run(string[] args)
    {
        var options = new CommandLine.Options(args);
        var port = int.TryParse(options.Optional("--port") ?? "7390", out var p) && p is > 0 and < 65536
            ? p : throw new CommandLineException("--port: a port number.");
        var version = options.Optional("--zabbix") ?? "70";
        int? tlsPort = options.Optional("--tls-port") is { } t
            ? int.TryParse(t, out var tp) && tp is > 0 and < 65536 ? tp : throw new CommandLineException("--tls-port: a port number.")
            : null;
        if (version is not ("60" or "70" or "80"))
        {
            throw new CommandLineException("--zabbix: 60, 70 or 80.");
        }
        options.EnsureAllUsed();

        var redmine = new FakeRedmine();
        var zabbix = new FakeZabbix();
        zabbix.Load(version);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        app.Map("/redmine/{**path}", context => ForwardAsync(context, "/redmine", redmine.HandleAsync));
        app.Map("/zabbix/{**path}", context => ForwardAsync(context, "/zabbix", zabbix.HandleAsync));
        app.MapGet("/", () => "PNMT's fake integrations: Redmine at /redmine, Zabbix at /zabbix.");
        Dev.FakeTlsSites? tls = null;
        if (tlsPort is { } port2)
        {
            // SSL certificate sensors (docs/ssl-certificate-plan.md §9): names by SNI on one port, the private CA's root, rotations.
            tls = new Dev.FakeTlsSites(port2);
            app.MapGet("/tls/private-ca.pem", () => Results.Text(tls.PrivateCaPem, "application/x-pem-file"));
            app.MapPost("/tls/rotate", (string name, string? key, string? issuer) =>
                tls.Rotate(name, keepKey: key == "keep", otherIssuer: issuer == "other") ? Results.NoContent() : Results.NotFound());
        }
        Console.WriteLine($"""
            A fake Redmine and Zabbix {zabbix.VersionText} on port {port}. For PNMT's .env (the host as PNMT reaches this one):
              Redmine__Url=http://<host>:{port}/redmine
              Redmine__ApiKey={FakeRedmine.Key}
              Redmine__Projects=it-support network
              Zabbix__Url=http://<host>:{port}/zabbix
              Zabbix__ApiToken={FakeZabbix.Token}
            """);
        if (tls is not null)
        {
            Console.WriteLine($"TLS on port {tlsPort}: {string.Join(", ", Dev.FakeTlsSites.Names)}; the private CA at /tls/private-ca.pem.");
        }
        app.Run();
        return 0;
    }

    /// <summary>The request as the fake's own, its path without the prefix; its answer back.</summary>
    private static async Task ForwardAsync(HttpContext context, string prefix,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle)
    {
        var path = context.Request.Path.Value![prefix.Length..];
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
            new Uri($"http://fake{(path.Length == 0 ? "/" : path)}{context.Request.QueryString}"));
        if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            request.Content = new ByteArrayContent(body.ToArray());
            if (context.Request.ContentType is { } type)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", type);
            }
        }
        foreach (var (name, values) in context.Request.Headers)
        {
            if (!name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && name is not ("Host" or "Transfer-Encoding"))
            {
                request.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }
        HttpResponseMessage response;
        try
        {
            response = await handle(request, context.RequestAborted);
        }
        catch (HttpRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType)
            {
                context.Response.ContentType = contentType.ToString();
            }
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }
}
