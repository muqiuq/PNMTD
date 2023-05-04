using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Models.Db;
using PNMTD.Models.Poco;
using PNMTD.Services;
using PNMTD.Tests;
using System.Diagnostics;
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

        if(!databaseAlreadyExists && Global.IsDevelopment)
        {
            Debug.WriteLine("Popuplating DB with test data");
            DbTestHelper.Populate(db);
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
        builder.Services.AddDbContext<PnmtdDbContext>(ServiceLifetime.Scoped);
        builder.Services.AddControllers();

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        builder.Services.AddHostedService<NotificiationService>();

        builder.Services.ConfigureHttpJsonOptions((j) =>
        {
            j.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;

        });

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();


        }

        

        app.UseHttpsRedirection();

        app.UseAuthorization();

        app.MapControllers();

        appTask = app.RunAsync();

        return app;
    }
}

