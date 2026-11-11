using PNMTD.Data;
using PNMTD.Import;

namespace PNMTD.Cli
{
    /// <summary>
    /// `pnmtd &lt;command&gt;`: administration without the web host (docs/webui-migration-plan.md §4.10).
    /// In the image, /usr/local/bin/pnmtd runs `dotnet /app/PNMTD.dll "$@"`; the deploy scripts call
    /// it through `docker compose exec pnmtd pnmtd …`. Exit codes: 0 done, 1 failed, 2 usage.
    /// </summary>
    public static class CommandLine
    {
        private static readonly string[] Commands = ["db", "import-sqlite", "help", "--help", "-h"];

        public static bool IsCommand(string arg) => Commands.Contains(arg);

        private const string Usage = """
            Usage: pnmtd <command>

              db migrate                            apply pending database migrations
              import-sqlite --from <pnmtd.db>       copy a v0.12 database into an empty PostgreSQL database
                  [--users <users.json>]            the WebApp's users, imported in the same transaction
                  [--source-time-zone <IANA zone>]  the zone v0.12 wrote its timestamps in (default: UTC)
                  [--reset]                         replace what an earlier import wrote (rehearsals)

            Without a command, pnmtd runs the server. The database comes from the Database:* settings
            (Database__Host, Database__Password, … in the environment).
            """;

        public static int Run(string[] args)
        {
            using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
            var logger = loggerFactory.CreateLogger("pnmtd");
            try
            {
                return args switch
                {
                    ["db", "migrate"] => Migrate(logger),
                    ["import-sqlite", .. var rest] => ImportSqlite(rest, loggerFactory),
                    _ => UsageError(args[0] is "help" or "--help" or "-h" ? null : $"Unknown command: {string.Join(' ', args)}"),
                };
            }
            catch (CommandLineException ex)
            {
                return UsageError(ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "pnmtd {Command} failed", args[0]);
                return 1;
            }
        }

        private static int UsageError(string? message)
        {
            if (message is not null)
            {
                Console.Error.WriteLine(message);
                Console.Error.WriteLine();
            }
            (message is null ? Console.Out : Console.Error).WriteLine(Usage);
            return message is null ? 0 : 2;
        }

        internal static IConfiguration Configuration()
        {
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
            return new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile($"appsettings.{environment}.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
        }

        private static PnmtdDbContext Database() =>
            PnmtdDbContext.Create(DatabaseSettings.ConnectionString(Configuration()));

        private static int Migrate(ILogger logger)
        {
            using var db = Database();
            DatabaseMigrator.Migrate(db, logger);
            logger.LogInformation("The database is up to date.");
            return 0;
        }

        private static int ImportSqlite(string[] args, ILoggerFactory loggerFactory)
        {
            var options = new Options(args);
            var importOptions = new SqliteImportOptions
            {
                SqlitePath = options.Required("--from"),
                UsersJsonPath = options.Optional("--users"),
                SourceTimeZone = options.Optional("--source-time-zone"),
                Reset = options.Flag("--reset"),
            };
            options.EnsureAllUsed();

            using var db = Database();
            DatabaseMigrator.Migrate(db, loggerFactory.CreateLogger("pnmtd"));
            var report = new SqliteImporter(db, loggerFactory.CreateLogger<SqliteImporter>()).Import(importOptions);
            Console.WriteLine(report.ToText());
            return report.Succeeded ? 0 : 1;
        }

        /// <summary>--name value and --flag, nothing more.</summary>
        private sealed class Options
        {
            private readonly Dictionary<string, string?> values = new();
            private readonly HashSet<string> used = new();

            public Options(string[] args)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CommandLineException($"Unexpected argument: {args[i]}");
                    }
                    var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                    values[args[i]] = hasValue ? args[++i] : null;
                }
            }

            public string Required(string name) =>
                Optional(name) ?? throw new CommandLineException($"{name} <value> is required.");

            public string? Optional(string name)
            {
                used.Add(name);
                if (!values.TryGetValue(name, out var value)) return null;
                return value ?? throw new CommandLineException($"{name} needs a value.");
            }

            public bool Flag(string name)
            {
                used.Add(name);
                if (!values.TryGetValue(name, out var value)) return false;
                return value is null ? true : throw new CommandLineException($"{name} takes no value.");
            }

            public void EnsureAllUsed()
            {
                var unknown = values.Keys.Where(k => !used.Contains(k)).ToList();
                if (unknown.Count > 0)
                {
                    throw new CommandLineException($"Unknown option(s): {string.Join(", ", unknown)}");
                }
            }
        }
    }

    public sealed class CommandLineException(string message) : Exception(message);
}
