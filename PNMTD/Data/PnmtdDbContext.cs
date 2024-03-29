﻿using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PNMTD.Models.Db;

namespace PNMTD.Data
{
    public class PnmtdDbContext : DbContext
    {
        public readonly bool inMemory;

        public DbSet<HostEntity> Hosts { get; set; }

        public DbSet<SensorEntity> Sensors { get; set; }

        public DbSet<EventEntity> Events { get; set; }

        public DbSet<NotificationRuleEntity> NotificationRules { get; set; }

        public DbSet<NotificationRuleEventEntity> NotificationRuleEvents { get; set; }

        public DbSet<NotificationRuleSensorEntity> NotificationRuleSensor { get; set; }

        public DbSet<MailInputRuleEntity> MailInputs { get; set; }

        public DbSet<KeyValueEntity> KeyValues { get; set; }

        public DbSet<MailLogEntity> MailLogs { get; set; }

        public DbSet<DnsZoneEntity> DnsZones { get; set; }
        public DbSet<DnsZoneEntryEntity> DnsZoneEntries { get; set; }
        public DbSet<DnsZoneLogEntryEntity> DnsZoneLogEntries { get; set; }

        public string DbPath { get; }

        public PnmtdDbContext(bool inMemory = false)
		{
            var path = "";
            if(!Global.IsDevelopment)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    path = Path.Join("/var/lib/pnmtd");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    path = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PNMTD");
                }
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            }
            DbPath = System.IO.Path.Join(path, "pnmtd.db");
            this.inMemory = inMemory;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseLazyLoadingProxies();
            //options.LogTo(x => Debug.WriteLine(x));
            if (inMemory)
            {
                options.UseSqlite("DataSource=myshareddb;mode=memory;cache=shared");
            }
            else
            {
                options.UseSqlite($"Data Source={DbPath}");
            }
            
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<KeyValueEntity>()
                .HasIndex(k => new { k.Key })
                .IsUnique(true);
            
            base.OnModelCreating(modelBuilder);
        }

    }
}

