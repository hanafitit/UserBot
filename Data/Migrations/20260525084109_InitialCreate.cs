using System;
using Microsoft.EntityFrameworkCore.Migrations;

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
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BaseText = table.Column<string>(type: "TEXT", nullable: false),
                    IsCurrent = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvertisingTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TargetChats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SlowModeSeconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 600),
                    LastSentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
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
            migrationBuilder.DropTable(
                name: "AdvertisingTemplates");

            migrationBuilder.DropTable(
                name: "ExecutionLogs");

            migrationBuilder.DropTable(
                name: "TargetChats");
        }
    }
}
