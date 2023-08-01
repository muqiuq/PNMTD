using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using PNMTD.Data;
using PNMTD.Helper;
using PNMTD.Models.Db;
using PNMTD.Models.Poco;
using PNMTD.Services;
using PNMTD.Tests;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PNMTD;

public partial class Program
{
    public static void Main(string[] args)
    {
        var db = new PnmtdDbContext();

        var databaseAlreadyExists = !db.Database.EnsureCreated();

        db.Database.Migrate();

        if (!databaseAlreadyExists && Global.IsDevelopment)
        {
            Debug.WriteLine("Popuplating DB with test data");
            DbTestHelper.Populate(db, ConfigurationHelper.InitConfiguration());
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
        builder.Services.AddControllers();

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        builder.Services.AddHostedService<NotificiationService>();

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
        builder.Logging.AddFile("pnmtd.log", fileSizeLimitBytes: 52430000, retainedFileCountLimit: 10);



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

