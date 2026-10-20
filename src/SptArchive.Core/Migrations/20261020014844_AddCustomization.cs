using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SptArchive.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_templates_kind",
                table: "mod_templates");

            migrationBuilder.CreateTable(
                name: "customizations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    spt_version = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    sides = table.Column<string[]>(type: "text[]", nullable: false),
                    body_part = table.Column<string>(type: "text", nullable: true),
                    prefab_path = table.Column<string>(type: "text", nullable: true),
                    available_as_default = table.Column<bool>(type: "boolean", nullable: false),
                    props = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customizations", x => new { x.spt_version, x.id });
                    table.ForeignKey(
                        name: "fk_customizations_spt_versions_spt_version",
                        column: x => x.spt_version,
                        principalTable: "spt_versions",
                        principalColumn: "version",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mod_customizations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    sides = table.Column<string[]>(type: "text[]", nullable: false),
                    body_part = table.Column<string>(type: "text", nullable: true),
                    prefab_path = table.Column<string>(type: "text", nullable: true),
                    available_as_default = table.Column<bool>(type: "boolean", nullable: false),
                    props = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_customizations", x => new { x.dataset_id, x.id });
                    table.ForeignKey(
                        name: "fk_mod_customizations_mod_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "mod_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_templates_kind",
                table: "mod_templates",
                sql: "kind IN ('item', 'customization', 'quest', 'trader')");

            migrationBuilder.CreateIndex(
                name: "ix_customizations_props",
                table: "customizations",
                column: "props")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_customizations_sides",
                table: "customizations",
                column: "sides")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_customizations_spt_version_body_part",
                table: "customizations",
                columns: new[] { "spt_version", "body_part" });

            migrationBuilder.CreateIndex(
                name: "ix_customizations_spt_version_name",
                table: "customizations",
                columns: new[] { "spt_version", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_mod_customizations_dataset_id_body_part",
                table: "mod_customizations",
                columns: new[] { "dataset_id", "body_part" });

            migrationBuilder.CreateIndex(
                name: "ix_mod_customizations_props",
                table: "mod_customizations",
                column: "props")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_mod_customizations_sides",
                table: "mod_customizations",
                column: "sides")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customizations");

            migrationBuilder.DropTable(
                name: "mod_customizations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_templates_kind",
                table: "mod_templates");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_templates_kind",
                table: "mod_templates",
                sql: "kind IN ('item', 'quest', 'trader')");
        }
    }
}
