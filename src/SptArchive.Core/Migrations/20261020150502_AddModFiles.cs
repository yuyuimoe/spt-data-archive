using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SptArchive.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddModFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_data_observations_outcome",
                table: "mod_data_observations");

            migrationBuilder.AddColumn<string>(
                name: "config_origin",
                table: "mod_datasets",
                type: "text",
                nullable: false,
                // Existing datasets were made from the configuration their mod shipped (ADR-0029).
                defaultValue: "shipped");

            migrationBuilder.AddColumn<string>(
                name: "config_origin_reason",
                table: "mod_datasets",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mod_files",
                columns: table => new
                {
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    shipped_sha256 = table.Column<string>(type: "text", nullable: true),
                    content_text = table.Column<string>(type: "text", nullable: true),
                    content = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_files", x => new { x.dataset_id, x.path });
                    table.CheckConstraint("ck_mod_files_kind", "kind IN ('assembly', 'bundle', 'data', 'config', 'text', 'other')");
                    table.ForeignKey(
                        name: "fk_mod_files_mod_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "mod_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_datasets_config_origin",
                table: "mod_datasets",
                sql: "config_origin IN ('shipped', 'curated')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_data_observations_outcome",
                table: "mod_data_observations",
                sql: "outcome IN ('archived', 'not_loaded', 'dependency_unresolved', 'outside_declared_range', 'crashed', 'timeout', 'no_server_data', 'loaded_but_disabled')");

            migrationBuilder.CreateIndex(
                name: "ix_mod_files_dataset_id_kind",
                table: "mod_files",
                columns: new[] { "dataset_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_mod_files_sha256",
                table: "mod_files",
                column: "sha256");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mod_files");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_datasets_config_origin",
                table: "mod_datasets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_data_observations_outcome",
                table: "mod_data_observations");

            migrationBuilder.DropColumn(
                name: "config_origin",
                table: "mod_datasets");

            migrationBuilder.DropColumn(
                name: "config_origin_reason",
                table: "mod_datasets");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_data_observations_outcome",
                table: "mod_data_observations",
                sql: "outcome IN ('archived', 'not_loaded', 'dependency_unresolved', 'outside_declared_range', 'crashed', 'timeout')");
        }
    }
}
