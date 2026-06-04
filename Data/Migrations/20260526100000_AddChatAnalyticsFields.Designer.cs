using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TestApp.Data;

#nullable disable

namespace TestApp.Data.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260526100000_AddChatAnalyticsFields")]
    partial class AddChatAnalyticsFields
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "8.0.11")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("TestApp.Data.Models.AdvertisingTemplate", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("BaseText")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<bool>("IsCurrent")
                        .HasColumnType("boolean");

                    b.HasKey("Id");

                    b.HasIndex("IsCurrent");

                    b.ToTable("AdvertisingTemplates", (string)null);
                });

            modelBuilder.Entity("TestApp.Data.Models.ExecutionLog", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<long>("ChatId")
                        .HasColumnType("bigint");

                    b.Property<string>("ErrorMessage")
                        .HasMaxLength(2048)
                        .HasColumnType("character varying(2048)");

                    b.Property<DateTime>("SentAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Status")
                        .IsRequired()
                        .HasMaxLength(64)
                        .HasColumnType("character varying(64)");

                    b.HasKey("Id");

                    b.HasIndex("ChatId");

                    b.HasIndex("SentAt");

                    b.ToTable("ExecutionLogs", (string)null);
                });

            modelBuilder.Entity("TestApp.Data.Models.TargetChat", b =>
                {
                    b.Property<long>("Id")
                        .HasColumnType("bigint");

                    b.Property<bool>("IsActive")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("boolean")
                        .HasDefaultValue(true);

                    b.Property<string>("LastErrorMessage")
                        .HasColumnType("text");

                    b.Property<DateTime?>("LastSentAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<DateTime?>("PostCountResetDateUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<int>("PostsPerDay")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasDefaultValue(5);

                    b.Property<int>("PostsTodayCount")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasDefaultValue(0);

                    b.Property<int>("SlowModeSeconds")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer")
                        .HasDefaultValue(600);

                    b.Property<string>("Title")
                        .IsRequired()
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("Id");

                    b.ToTable("TargetChats", (string)null);
                });
#pragma warning restore 612, 618
        }
    }
}
