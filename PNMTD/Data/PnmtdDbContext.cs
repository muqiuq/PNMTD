using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using PNMTD.Models.Db;

namespace PNMTD.Data
{
    public class PnmtdDbContext : DbContext
    {
        private readonly bool inMemory;

        public DbSet<HostEntity> Hosts { get; set; }

        public DbSet<SensorEntity> Sensors { get; set; }

        public DbSet<EventEntity> Events { get; set; }

        public string DbPath { get; }

        public PnmtdDbContext(bool inMemory = false)
		{
            var folder = Environment.SpecialFolder.LocalApplicationData;
            var path = Environment.GetFolderPath(folder);
            path = "";
            DbPath = System.IO.Path.Join(path, "pnmtd.db");
            this.inMemory = inMemory;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            if(inMemory)
            {
                options.UseSqlite("DataSource=myshareddb;mode=memory;cache=shared");
            }
            else
            {
                options.UseSqlite($"Data Source={DbPath}");
            }
            
        }

    }
}

