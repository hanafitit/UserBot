using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TestApp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPostsPerDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Уже включено в InitialCreate для Postgres — миграция пустая для совместимости истории
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "PostsPerDay", table: "TargetChats");
            migrationBuilder.DropColumn(name: "PostsTodayCount", table: "TargetChats");
            migrationBuilder.DropColumn(name: "PostCountResetDateUtc", table: "TargetChats");
        }
    }
}
