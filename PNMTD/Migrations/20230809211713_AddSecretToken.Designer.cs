﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PNMTD.Data;

#nullable disable

namespace PNMTD.Migrations
{
    [DbContext(typeof(PnmtdDbContext))]
    [Migration("20230809211713_AddSecretToken")]
    partial class AddSecretToken
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "7.0.10")
                .HasAnnotation("Proxies:ChangeTracking", false)
                .HasAnnotation("Proxies:CheckEquality", false)
                .HasAnnotation("Proxies:LazyLoading", true);

            modelBuilder.Entity("PNMTD.Models.Db.EventEntity", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("TEXT");

                    b.Property<int>("Code")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("Created")
                        .HasColumnType("TEXT");

                    b.Property<string>("Message")
                        .HasColumnType("TEXT");

                    b.Property<Guid>("SensorId")
                        .HasColumnType("TEXT");

                    b.Property<string>("Source")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("SensorId");

                    b.ToTable("events");
                });

            modelBuilder.Entity("PNMTD.Models.Db.HostEntity", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("Created")
                        .HasColumnType("TEXT");

                    b.Property<bool>("Enabled")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Location")
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("Notes")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("hosts");
                });

            modelBuilder.Entity("PNMTD.Models.Db.NotificationRuleEntity", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("TEXT");

                    b.Property<bool>("Enabled")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("Recipient")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int>("Type")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.ToTable("notificationrules");
                });

            modelBuilder.Entity("PNMTD.Models.Db.NotificationRuleEventEntity", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("Created")
                        .HasColumnType("TEXT");

                    b.Property<Guid>("EventId")
                        .HasColumnType("TEXT");

                    b.Property<bool>("NoAction")
                        .HasColumnType("INTEGER");

                    b.Property<Guid>("NotificationRuleId")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("EventId");

                    b.HasIndex("NotificationRuleId");

                    b.ToTable("notificationruleevent");
                });

            modelBuilder.Entity("PNMTD.Models.Db.NotificationRuleSensorEntity", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("TEXT");

                    b.Property<Guid>("NotificationRuleId")
                        .HasColumnType("TEXT");

                    b.Property<Guid>("SensorId")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("NotificationRuleId");

                    b.HasIndex("SensorId");

                    b.ToTable("NotificationRuleSensor");
                });

            modelBuilder.Entity("PNMTD.Models.Db.SensorEntity", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("Created")
                        .HasColumnType("TEXT");

                    b.Property<bool>("Enabled")
                        .HasColumnType("INTEGER");

                    b.Property<int>("GracePeriod")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Interval")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<Guid?>("OlderSiblingId")
                        .HasColumnType("TEXT");

                    b.Property<string>("Parameters")
                        .HasColumnType("TEXT");

                    b.Property<Guid>("ParentId")
                        .HasColumnType("TEXT");

                    b.Property<string>("SecretToken")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("TextId")
                        .HasColumnType("TEXT");

                    b.Property<int>("Type")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("OlderSiblingId");

                    b.HasIndex("ParentId");

                    b.ToTable("sensors");
                });

            modelBuilder.Entity("PNMTD.Models.Db.EventEntity", b =>
                {
                    b.HasOne("PNMTD.Models.Db.SensorEntity", "Sensor")
                        .WithMany()
                        .HasForeignKey("SensorId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Sensor");
                });

            modelBuilder.Entity("PNMTD.Models.Db.NotificationRuleEventEntity", b =>
                {
                    b.HasOne("PNMTD.Models.Db.EventEntity", "Event")
                        .WithMany("NotificationRuleEvents")
                        .HasForeignKey("EventId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("PNMTD.Models.Db.NotificationRuleEntity", "NotificationRule")
                        .WithMany()
                        .HasForeignKey("NotificationRuleId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Event");

                    b.Navigation("NotificationRule");
                });

            modelBuilder.Entity("PNMTD.Models.Db.NotificationRuleSensorEntity", b =>
                {
                    b.HasOne("PNMTD.Models.Db.NotificationRuleEntity", "NotificationRule")
                        .WithMany("SubscribedSensors")
                        .HasForeignKey("NotificationRuleId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("PNMTD.Models.Db.SensorEntity", "Sensor")
                        .WithMany("SubscribedByNotifications")
                        .HasForeignKey("SensorId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("NotificationRule");

                    b.Navigation("Sensor");
                });

            modelBuilder.Entity("PNMTD.Models.Db.SensorEntity", b =>
                {
                    b.HasOne("PNMTD.Models.Db.SensorEntity", "OlderSibling")
                        .WithMany()
                        .HasForeignKey("OlderSiblingId");

                    b.HasOne("PNMTD.Models.Db.HostEntity", "Parent")
                        .WithMany("Sensors")
                        .HasForeignKey("ParentId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("OlderSibling");

                    b.Navigation("Parent");
                });

            modelBuilder.Entity("PNMTD.Models.Db.EventEntity", b =>
                {
                    b.Navigation("NotificationRuleEvents");
                });

            modelBuilder.Entity("PNMTD.Models.Db.HostEntity", b =>
                {
                    b.Navigation("Sensors");
                });

            modelBuilder.Entity("PNMTD.Models.Db.NotificationRuleEntity", b =>
                {
                    b.Navigation("SubscribedSensors");
                });

            modelBuilder.Entity("PNMTD.Models.Db.SensorEntity", b =>
                {
                    b.Navigation("SubscribedByNotifications");
                });
#pragma warning restore 612, 618
        }
    }
}
