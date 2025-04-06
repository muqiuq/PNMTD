using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using PNMTD.Data;
using PNMTD.Helper;
using PNMTD.Models.Db;
using PNMTD.Models.Poco;
using PNMTD.Notifications;
using PNMTD.Tasks;
using PNMTD.Tests;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PNMTD.Lib.Authentification;
using PNMTD.Models.Helper;

namespace PNMTD;

public partial class Program
{
    private static readonly ILogger _logger = LogManager.CreateLogger<Program>();

    public static void Main(string[] args)
    {
        var db = new PnmtdDbContext();

        var databaseAlreadyExists = db.Database.CanConnect();

        db.Database.Migrate();

        if (!databaseAlreadyExists && Global.IsDevelopment)
        {
            Debug.WriteLine("Popuplating DB with test data");
            DbTestHelper.Populate(db, ConfigurationHelper.InitConfiguration());
        }
        if(databaseAlreadyExists)
        {
            _logger.LogInformation($"Created DB in {db.DbPath}");
        }

        db.RunCustomMigrations();

        db.Dispose();

        var app = RunApi(args, out var appTask);
        appTask.Wait();
    }

    public static WebApplication RunApi(string[] args, out Task appTask, bool test = false)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<PnmtdDbContext>(ServiceLifetime.Transient);

        builder.Services.AddControllers()
            .AddMvcOptions(options => options.Filters.Add(new AuthorizeFilter()));

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<NotificationService>();

        var configJsonPath = Path.Combine(GlobalConfiguration.LinuxBasePath, "config.json");

        if (!Global.IsDevelopment)
        {
            builder.Configuration.AddEnvironmentVariables();
        }

        if (!Global.IsDevelopment && RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists(configJsonPath))
        {
            builder.Configuration.AddJsonFile(configJsonPath, true, true);
        }

        builder.Services.AddHostedService<NotificationTask>();
        builder.Services.AddHostedService<HeartbeatCheckTask>();
        builder.Services.AddHostedService<PingCheckTask>();
        builder.Services.AddHostedService<MailInboxCheckTask>();
        builder.Services.AddHostedService<MailProcessTask>();
        builder.Services.AddHostedService<TimespanConditionsCheckTask>();
        builder.Services.AddHostedService<UplinkCheckTask>();
        builder.Services.AddHostedService<DnsCheckTask>();

        builder.Services.AddSwaggerGen(option =>
        {
            option.SwaggerDoc("v1", new OpenApiInfo { Title = "PNMTD", Version = "v1" });
            option.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                In = ParameterLocation.Header,
                Description = "Please enter a valid token",
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                BearerFormat = "JWT",
                Scheme = "Bearer"
            });
            option.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type=ReferenceType.SecurityScheme,
                                Id="Bearer"
                            }
                        },
                        new string[]{}
                    }
                });
        });

        if(!string.IsNullOrWhiteSpace(builder.Configuration["Proxy"]))
        {
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.RequireHeaderSymmetry = false;
                options.ForwardLimit = null;
                // Example format: ::ffff:172.20.10.20
                options.KnownProxies.Add(IPAddress.Parse(builder.Configuration["Proxy"]));
            });
        }

        if (!string.IsNullOrWhiteSpace(builder.Configuration["Identity"]))
        {
            ServerInfo._Identity = builder.Configuration["Identity"] ?? ServerInfo._Identity;
        }

        var jwtTokenConfig = new JwtTokenConfig()
        {
            Issuer = builder.Configuration["Jwt:Issuer"]!,
            Audience = builder.Configuration["Jwt:Audience"]!,
            Key = builder.Configuration["Jwt:Key"]!
        };

        if (string.IsNullOrEmpty(jwtTokenConfig.Issuer) || string.IsNullOrEmpty(jwtTokenConfig.Audience) ||
            string.IsNullOrEmpty(jwtTokenConfig.Key))
        {
            _logger.LogError("Missing Jwt:Issuer, Jwt:Audience or Jwt:Key. Aborting startup");
            throw new ArgumentException("Missing Jwt:Issuer, Jwt:Audience or Jwt:Key. Aborting startup");
        }

        builder.Services.AddSingleton<JwtTokenConfig>(jwtTokenConfig);

        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        }).AddJwtBearer(o =>
        {
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = jwtTokenConfig.Issuer,
                ValidAudience = jwtTokenConfig.Audience,
                IssuerSigningKey = new SymmetricSecurityKey
                    (JwtTokenHelper.ExpandKey(jwtTokenConfig.Key)),
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = false,
                ValidateIssuerSigningKey = true
            };
        });
        builder.Services.AddAuthorization();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddDebug();

        var logFolder = "";
        if (!Global.IsDevelopment && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            logFolder = Path.Combine(GlobalConfiguration.LinuxBasePath, "logs");
            if (!Directory.Exists(logFolder))
            {
                Directory.CreateDirectory(logFolder);
            }
        }

        builder.Logging.AddFile(Path.Combine(logFolder, "pnmtd.log"), fileSizeLimitBytes: 52430000, retainedFileCountLimit: 10);

        if(Global.IsDevelopment)
        {
            builder.WebHost.UseUrls("http://localhost:7327","https://localhost:7328");
        }

        builder.Services.ConfigureHttpJsonOptions((j) =>
        {
            j.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;

        });

        var app = builder.Build();

        Global.App = app;

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.UseForwardedHeaders();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers();

        appTask = app.RunAsync();

        return app;
    }
}

