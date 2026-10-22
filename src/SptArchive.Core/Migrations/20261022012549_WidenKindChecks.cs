using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SptArchive.Core.Migrations
{
    /// <summary>
    /// ADR-0044: the ten kinds of the seven files that held no row widen the four kind checks. No table is
    /// renamed, no column is added and no row is copied. <c>Down</c> deletes the rows of the new kinds first, and
    /// restores the old lists verbatim.
    /// </summary>
    public partial class WidenKindChecks : Migration
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
                name: "ck_mod_database_objects_kind",
                table: "mod_database_objects");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_change_paths_kind",
                table: "mod_change_paths");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_templates_kind",
                table: "mod_templates",
                sql: "kind IN ('item', 'quest', 'trader', 'customization', 'assort', 'location', 'bot', 'hideout', 'price', 'handbook', 'dialogue', 'preset', 'bot-preset', 'scattering', 'global', 'setting', 'profile', 'achievement', 'storage', 'character')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_id_map_kind",
                table: "mod_id_maps",
                sql: "kind IN ('item', 'quest', 'trader', 'customization', 'assort', 'location', 'bot', 'hideout', 'price', 'handbook', 'dialogue', 'preset', 'bot-preset', 'scattering', 'global', 'setting', 'profile', 'achievement', 'storage', 'character')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_database_objects_kind",
                table: "mod_database_objects",
                sql: "kind IN ('location', 'bot', 'hideout', 'price', 'handbook', 'dialogue', 'preset', 'bot-preset', 'scattering', 'global', 'setting', 'profile', 'achievement', 'storage', 'character')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_change_paths_kind",
                table: "mod_change_paths",
                sql: "kind IN ('item', 'quest', 'trader', 'customization', 'assort', 'location', 'bot', 'hideout', 'price', 'handbook', 'dialogue', 'preset', 'bot-preset', 'scattering', 'global', 'setting', 'profile', 'achievement', 'storage', 'character')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A row of a new kind cannot live in a table whose kind check does not name it.
            migrationBuilder.Sql("DELETE FROM mod_change_paths WHERE kind IN ('dialogue', 'preset', 'bot-preset', 'scattering', 'global', 'setting', 'profile', 'achievement', 'storage', 'character');");
            migrationBuilder.Sql("DELETE FROM mod_templates WHERE kind IN ('dialogue', 'preset', 'bot-preset', 'scattering', 'global', 'setting', 'profile', 'achievement', 'storage', 'character');");
            migrationBuilder.Sql("DELETE FROM mod_id_maps WHERE kind IN ('dialogue', 'preset', 'bot-preset', 'scattering', 'global', 'setting', 'profile', 'achievement', 'storage', 'character');");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_templates_kind",
                table: "mod_templates");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_id_map_kind",
                table: "mod_id_maps");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_database_objects_kind",
                table: "mod_database_objects");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mod_change_paths_kind",
                table: "mod_change_paths");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_templates_kind",
                table: "mod_templates",
                sql: "kind IN ('item', 'customization', 'quest', 'trader', 'assort', 'location', 'bot', 'hideout', 'price', 'handbook')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_id_map_kind",
                table: "mod_id_maps",
                sql: "kind IN ('item', 'customization', 'quest', 'trader', 'assort', 'location', 'bot', 'hideout', 'price', 'handbook')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_database_objects_kind",
                table: "mod_database_objects",
                sql: "kind IN ('location', 'bot', 'hideout', 'price', 'handbook')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mod_change_paths_kind",
                table: "mod_change_paths",
                sql: "kind IN ('item', 'customization', 'quest', 'trader', 'assort', 'location', 'bot', 'hideout', 'price', 'handbook')");
        }
    }
}
