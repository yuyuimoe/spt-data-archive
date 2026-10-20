using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SptArchive.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddModIdMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mod_id_maps",
                columns: table => new
                {
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    raw_id = table.Column<string>(type: "text", nullable: false),
                    address = table.Column<string>(type: "text", nullable: false),
                    pinned = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_id_maps", x => new { x.dataset_id, x.kind, x.raw_id });
                    table.CheckConstraint("ck_mod_id_map_kind", "kind IN ('item', 'customization', 'quest', 'trader')");
                    table.ForeignKey(
                        name: "fk_mod_id_maps_mod_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "mod_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mod_id_maps_dataset_id_address",
                table: "mod_id_maps",
                columns: new[] { "dataset_id", "address" });

            migrationBuilder.CreateIndex(
                name: "ix_mod_id_maps_kind_raw_id",
                table: "mod_id_maps",
                columns: new[] { "kind", "raw_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mod_id_maps");
        }
    }
}
