using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SptArchive.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRunHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "heartbeat_at",
                table: "archive_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_archive_runs_lease",
                table: "archive_runs",
                column: "heartbeat_at",
                filter: "status = 'running'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_archive_runs_status",
                table: "archive_runs",
                sql: "status IN ('running', 'succeeded', 'failed', 'interrupted')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_archive_runs_lease",
                table: "archive_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_archive_runs_status",
                table: "archive_runs");

            migrationBuilder.DropColumn(
                name: "heartbeat_at",
                table: "archive_runs");
        }
    }
}
