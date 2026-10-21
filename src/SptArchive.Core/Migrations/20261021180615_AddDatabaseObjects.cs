using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SptArchive.Core.Migrations
{
    /// <summary>
    /// ADR-0041: a location, a bot type, a hideout part, a price and a handbook entry become templates, so
    /// the three kind checks widen, and <c>mod_database_objects</c> keeps the source of an added one. No table
    /// is renamed and no row is copied.
    /// </summary>
    public partial class AddDatabaseObjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_templates_kind",
                table: "mod_templates");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_id_map_kind",
                table: "mod_id_maps");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_change_paths_kind",
                table: "mod_change_paths");

            migrationBuilder.CreateTable(
                name: "mod_database_objects",
                columns: table => new
                {
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    template_id = table.Column<string>(type: "text", nullable: false),
                    part = table.Column<string>(type: "text", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_database_objects", x => new { x.dataset_id, x.kind, x.template_id, x.part });
                    table.CheckConstraint("ck_mod_database_objects_kind", "kind IN ('location', 'bot', 'hideout', 'price', 'handbook')");
                    table.ForeignKey(
                        name: "fk_mod_database_objects_mod_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "mod_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_mod_database_objects_mod_templates_dataset_id_kind_template",
                        columns: x => new { x.dataset_id, x.kind, x.template_id },
                        principalTable: "mod_templates",
                        principalColumns: new[] { "dataset_id", "kind", "template_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_templates_kind",
                table: "mod_templates",
                sql: "kind IN ('item', 'customization', 'quest', 'trader', 'assort', 'location', 'bot', 'hideout', 'price', 'handbook')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_id_map_kind",
                table: "mod_id_maps",
                sql: "kind IN ('item', 'customization', 'quest', 'trader', 'assort', 'location', 'bot', 'hideout', 'price', 'handbook')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_change_paths_kind",
                table: "mod_change_paths",
                sql: "kind IN ('item', 'customization', 'quest', 'trader', 'assort', 'location', 'bot', 'hideout', 'price', 'handbook')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mod_database_objects");

            // A row of a new kind cannot live in a table whose kind check does not name it.
            migrationBuilder.Sql("DELETE FROM mod_change_paths WHERE kind IN ('location', 'bot', 'hideout', 'price', 'handbook');");
            migrationBuilder.Sql("DELETE FROM mod_templates WHERE kind IN ('location', 'bot', 'hideout', 'price', 'handbook');");
            migrationBuilder.Sql("DELETE FROM mod_id_maps WHERE kind IN ('location', 'bot', 'hideout', 'price', 'handbook');");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_templates_kind",
                table: "mod_templates");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_id_map_kind",
                table: "mod_id_maps");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_change_paths_kind",
                table: "mod_change_paths");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_templates_kind",
                table: "mod_templates",
                sql: "kind IN ('item', 'customization', 'quest', 'trader', 'assort')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_id_map_kind",
                table: "mod_id_maps",
                sql: "kind IN ('item', 'customization', 'quest', 'trader', 'assort')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_change_paths_kind",
                table: "mod_change_paths",
                sql: "kind IN ('item', 'customization', 'quest', 'trader', 'assort')");
        }
    }
}
