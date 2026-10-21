using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SptArchive.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddObjectRowSize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "bytes",
                table: "mod_database_objects",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // A row that a version before this one wrote has no size. The size of the document is the
            // approximation that costs one pass over the table and no document: PostgreSQL renders jsonb
            // with a space after every comma and colon, so the number is a little larger than the file
            // that the run wrote. The Api refuses a document near a limit of megabytes, so the difference
            // does not change an answer.
            migrationBuilder.Sql("UPDATE mod_database_objects SET bytes = octet_length(data::text);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bytes",
                table: "mod_database_objects");
        }
    }
}
