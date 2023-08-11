using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.HttpResults;
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
using PNMTD.Services;
using PNMTD.Tests;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

        if (!Global.IsDevelopment && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            builder.Configuration.AddJsonFile(Path.Combine(GlobalConfiguration.LinuxBasePath,"config.json"), true, true);
        }

        builder.Services.AddHostedService<NotificiationService>();
        builder.Services.AddHostedService<HeartbeatCheckTask>();
        builder.Services.AddHostedService<PingCheckTask>();

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

        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        }).AddJwtBearer(o =>
        {
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = builder.Configuration["Jwt:Issuer"],
                ValidAudience = builder.Configuration["Jwt:Audience"],
                IssuerSigningKey = new SymmetricSecurityKey
                    (Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"])),
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

        GlobalConfiguration.Init(app.Configuration);

        Global.App = app;

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers();

        appTask = app.RunAsync();

        return app;
    }
}

