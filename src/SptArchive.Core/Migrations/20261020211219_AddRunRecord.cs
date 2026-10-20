using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SptArchive.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRunRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "created_by_run_id",
                table: "mod_datasets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "archive_run_id",
                table: "mod_data_observations",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "target_unverified",
                table: "mod_data_observations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "baseline_hash",
                table: "archive_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "baseline_release",
                table: "archive_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "archive_runs",
                type: "text",
                nullable: false,
                defaultValue: "official");

            // Before this change only a run import wrote observations, so a run that an observation names is a mod run.
            migrationBuilder.Sql(
                "UPDATE archive_runs SET kind = 'mod' WHERE id IN " +
                "(SELECT archive_run_id FROM mod_data_observations WHERE archive_run_id IS NOT NULL);");

            migrationBuilder.AddColumn<string>(
                name: "purpose",
                table: "archive_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "result",
                table: "archive_runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_mod_guid",
                table: "archive_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_version",
                table: "archive_runs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_mod_datasets_created_by_run_id",
                table: "mod_datasets",
                column: "created_by_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_mod_data_observations_archive_run_id",
                table: "mod_data_observations",
                column: "archive_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_runs_target_mod_guid_started_at",
                table: "archive_runs",
                columns: new[] { "target_mod_guid", "started_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_archive_runs_kind",
                table: "archive_runs",
                sql: "kind IN ('official', 'mod', 'probe')");

            migrationBuilder.AddForeignKey(
                name: "fk_mod_data_observations_archive_runs_archive_run_id",
                table: "mod_data_observations",
                column: "archive_run_id",
                principalTable: "archive_runs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_mod_datasets_archive_runs_created_by_run_id",
                table: "mod_datasets",
                column: "created_by_run_id",
                principalTable: "archive_runs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_mod_data_observations_archive_runs_archive_run_id",
                table: "mod_data_observations");

            migrationBuilder.DropForeignKey(
                name: "fk_mod_datasets_archive_runs_created_by_run_id",
                table: "mod_datasets");

            migrationBuilder.DropIndex(
                name: "ix_mod_datasets_created_by_run_id",
                table: "mod_datasets");

            migrationBuilder.DropIndex(
                name: "ix_mod_data_observations_archive_run_id",
                table: "mod_data_observations");

            migrationBuilder.DropIndex(
                name: "ix_archive_runs_target_mod_guid_started_at",
                table: "archive_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_archive_runs_kind",
                table: "archive_runs");

            migrationBuilder.DropColumn(
                name: "created_by_run_id",
                table: "mod_datasets");

            migrationBuilder.DropColumn(
                name: "target_unverified",
                table: "mod_data_observations");

            migrationBuilder.DropColumn(
                name: "baseline_hash",
                table: "archive_runs");

            migrationBuilder.DropColumn(
                name: "baseline_release",
                table: "archive_runs");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "archive_runs");

            migrationBuilder.DropColumn(
                name: "purpose",
                table: "archive_runs");

            migrationBuilder.DropColumn(
                name: "result",
                table: "archive_runs");

            migrationBuilder.DropColumn(
                name: "target_mod_guid",
                table: "archive_runs");

            migrationBuilder.DropColumn(
                name: "target_version",
                table: "archive_runs");

            migrationBuilder.AlterColumn<Guid>(
                name: "archive_run_id",
                table: "mod_data_observations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }
    }
}
