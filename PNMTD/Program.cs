using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Data;
using PNMTD.Models.Db;
using PNMTD.Models.Poco;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PNMTD;

public class Program
{
    public static void Main(string[] args)
    {
        var db = new PnmtdDbContext();

        if(db.Database.EnsureCreated())
        {
            var random = new Random();
            for (int a = 0; a < 10; a++)
            {
                var h1 = new HostEntity()
                {
                    Created = DateTime.Now,
                    Enabled = true,
                    Id = Guid.NewGuid(),
                    Name = $"Testhost {a}"
                };

                db.Hosts.Add(h1);

                for (int b = 0; b < 5; b++)
                {
                    var s1 = new SensorEntity()
                    {
                        Id = Guid.NewGuid(),
                        Created = DateTime.Now,
                        Enabled = true,
                        Name = "Ping",
                        Parent = h1,
                        Type = SensorType.PING
                    };
                    db.Sensors.Add(s1);
                    for (int c = 0; c < 5; c++)
                    {

                        var code = random.Next(0, 999);
                        var e1 = new EventEntity()
                        {
                            Id = Guid.NewGuid(),
                            Created = DateTime.Now,
                            Code = code,
                            Message = "",
                            Sensor = s1
                        };
                        db.Events.Add(e1);
                    }
                }
            }

            db.SaveChanges();
        }

        Debug.WriteLine(db.Hosts.First().Id);
        Debug.WriteLine(db.Sensors.First().Id);

        RunApi(args, db);
    }

    public static void RunApi(string[] args, PnmtdDbContext db)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<PnmtdDbContext>(db);
        builder.Services.AddControllers();

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        ;

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

        app.Run();
    }
}

