using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TestApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdvertisingTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BaseText = table.Column<string>(type: "text", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvertisingTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TargetChats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SlowModeSeconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 600),
                    LastSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    PostsPerDay = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    PostsTodayCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    PostCountResetDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetChats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdvertisingTemplates_IsCurrent",
                table: "AdvertisingTemplates",
                column: "IsCurrent");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionLogs_ChatId",
                table: "ExecutionLogs",
                column: "ChatId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionLogs_SentAt",
                table: "ExecutionLogs",
                column: "SentAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AdvertisingTemplates");
            migrationBuilder.DropTable(name: "ExecutionLogs");
            migrationBuilder.DropTable(name: "TargetChats");
        }
    }
}
