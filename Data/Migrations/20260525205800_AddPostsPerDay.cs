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
            migrationBuilder.AddColumn<int>(
                name: "PostsPerDay",
                table: "TargetChats",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "PostsTodayCount",
                table: "TargetChats",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PostCountResetDateUtc",
                table: "TargetChats",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PostsPerDay",
                table: "TargetChats");

            migrationBuilder.DropColumn(
                name: "PostsTodayCount",
                table: "TargetChats");

            migrationBuilder.DropColumn(
                name: "PostCountResetDateUtc",
                table: "TargetChats");
        }
    }
}
