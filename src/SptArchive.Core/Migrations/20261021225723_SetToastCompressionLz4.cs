using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SptArchive.Core.Migrations
{
    /// <summary>
    /// Stores every jsonb column of the model with lz4 instead of the default pglz (ADR-0041).
    /// </summary>
    /// <remarks>
    /// PostgreSQL moves a value that does not fit in its row into a TOAST table, and it compresses that
    /// value on the way, pglz by default. lz4 stores the same value in a few percent fewer bytes and
    /// assembles it about two and a half times faster. Measured against PostgreSQL 17.11 on 2026-10-21
    /// with a real map file of 132,164,983 bytes: 47,308,800 bytes and 138 ms with pglz, 45,408,256 bytes
    /// and 56 ms with lz4.
    ///
    /// The setting is a property of the column, so a row that an earlier migration wrote keeps pglz until
    /// something writes it again. An import from now on writes the values that every reader waits for.
    /// </remarks>
    public partial class SetToastCompressionLz4 : Migration
    {
        private static readonly (string Table, string Column)[] JsonbColumns =
        [
            ("archive_runs", "result"),
            ("customizations", "props"),
            ("items", "props"),
            ("mod_change_paths", "new_value"),
            ("mod_change_paths", "old_value"),
            ("mod_customizations", "props"),
            ("mod_data_observations", "dependency_set"),
            ("mod_database_objects", "data"),
            ("mod_datasets", "row_counts"),
            ("mod_datasets", "table_hashes"),
            ("mod_files", "content"),
            ("mod_items", "props"),
            ("mod_quests", "data"),
            ("mod_trader_assort_items", "barter_scheme"),
            ("mod_trader_assort_items", "upd"),
            ("mod_traders", "base"),
            ("mod_traders", "dialogue"),
            ("mod_traders", "services"),
            ("mod_traders", "suits"),
            ("mods", "metadata"),
            ("quests", "data"),
            ("trader_assort_items", "barter_scheme"),
            ("trader_assort_items", "upd"),
            ("traders", "base"),
            ("traders", "dialogue"),
            ("traders", "services"),
            ("traders", "suits"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in JsonbColumns)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ALTER COLUMN {column} SET COMPRESSION lz4;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in JsonbColumns)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ALTER COLUMN {column} SET COMPRESSION pglz;");
            }
        }
    }
}
