using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SptArchive.Core.Migrations
{
    /// <summary>
    /// ADR-0032, decisions 1 and 4: <c>mod_item_changes</c> becomes <c>mod_change_paths</c>, and every kind
    /// of data states the paths of its changed values, not only an item. The rename keeps the rows that
    /// are already stored: a dataset is append-only (ADR-0018).
    /// </summary>
    public partial class AddChangePaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One statement per rename: Postgres renames the table and its constraints in place, so no
            // row is copied and no row is dropped.
            migrationBuilder.Sql("ALTER TABLE mod_item_changes RENAME TO mod_change_paths;");
            migrationBuilder.Sql("ALTER TABLE mod_change_paths RENAME CONSTRAINT pk_mod_item_changes TO pk_mod_change_paths;");
            migrationBuilder.Sql(
                "ALTER TABLE mod_change_paths RENAME CONSTRAINT " +
                "fk_mod_item_changes_mod_templates_dataset_id_kind_template_id TO " +
                "fk_mod_change_paths_mod_templates_dataset_id_kind_template_id;");
            migrationBuilder.Sql("ALTER TABLE mod_change_paths RENAME CONSTRAINT ck_mod_item_changes_op TO ck_mod_change_paths_op;");

            // The kind check widens to every kind that states paths.
            migrationBuilder.Sql("ALTER TABLE mod_change_paths DROP CONSTRAINT ck_mod_item_changes_kind;");
            migrationBuilder.Sql(
                "ALTER TABLE mod_change_paths ADD CONSTRAINT ck_mod_change_paths_kind " +
                "CHECK (kind IN ('item', 'customization', 'quest', 'trader', 'assort'));");

            // An offer is a template of its own, and its id is an address the archive derives.
            migrationBuilder.Sql("ALTER TABLE mod_templates DROP CONSTRAINT ck_mod_templates_kind;");
            migrationBuilder.Sql(
                "ALTER TABLE mod_templates ADD CONSTRAINT ck_mod_templates_kind " +
                "CHECK (kind IN ('item', 'customization', 'quest', 'trader', 'assort'));");

            migrationBuilder.Sql("ALTER TABLE mod_id_maps DROP CONSTRAINT ck_mod_id_map_kind;");
            migrationBuilder.Sql(
                "ALTER TABLE mod_id_maps ADD CONSTRAINT ck_mod_id_map_kind " +
                "CHECK (kind IN ('item', 'customization', 'quest', 'trader', 'assort'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // An offer row cannot live in a table whose kind check names items only, so the rows of that
            // kind go before the table goes back to its old name.
            migrationBuilder.Sql("DELETE FROM mod_change_paths WHERE kind = 'assort';");
            migrationBuilder.Sql("DELETE FROM mod_templates WHERE kind = 'assort';");
            migrationBuilder.Sql("DELETE FROM mod_id_maps WHERE kind = 'assort';");

            migrationBuilder.Sql("ALTER TABLE mod_change_paths DROP CONSTRAINT ck_mod_change_paths_kind;");
            migrationBuilder.Sql("ALTER TABLE mod_change_paths ADD CONSTRAINT ck_mod_item_changes_kind CHECK (kind = 'item');");
            migrationBuilder.Sql("ALTER TABLE mod_change_paths RENAME CONSTRAINT ck_mod_change_paths_op TO ck_mod_item_changes_op;");
            migrationBuilder.Sql(
                "ALTER TABLE mod_change_paths RENAME CONSTRAINT " +
                "fk_mod_change_paths_mod_templates_dataset_id_kind_template_id TO " +
                "fk_mod_item_changes_mod_templates_dataset_id_kind_template_id;");
            migrationBuilder.Sql("ALTER TABLE mod_change_paths RENAME CONSTRAINT pk_mod_change_paths TO pk_mod_item_changes;");
            migrationBuilder.Sql("ALTER TABLE mod_change_paths RENAME TO mod_item_changes;");

            migrationBuilder.Sql("ALTER TABLE mod_templates DROP CONSTRAINT ck_mod_templates_kind;");
            migrationBuilder.Sql(
                "ALTER TABLE mod_templates ADD CONSTRAINT ck_mod_templates_kind " +
                "CHECK (kind IN ('item', 'customization', 'quest', 'trader'));");

            migrationBuilder.Sql("ALTER TABLE mod_id_maps DROP CONSTRAINT ck_mod_id_map_kind;");
            migrationBuilder.Sql(
                "ALTER TABLE mod_id_maps ADD CONSTRAINT ck_mod_id_map_kind " +
                "CHECK (kind IN ('item', 'customization', 'quest', 'trader'));");
        }
    }
}
