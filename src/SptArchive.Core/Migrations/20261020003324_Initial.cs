using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SptArchive.Core.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "archive_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_hash = table.Column<string>(type: "text", nullable: false),
                    spt_version = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archive_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "languages",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_languages", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "mod_datasets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mod_guid = table.Column<string>(type: "text", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    table_hashes = table.Column<string>(type: "jsonb", nullable: false),
                    row_counts = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_datasets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mods",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mod_guid = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    author = table.Column<string>(type: "text", nullable: true),
                    spt_version_range = table.Column<string>(type: "text", nullable: true),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    license = table.Column<string>(type: "text", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mods", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "spt_versions",
                columns: table => new
                {
                    version = table.Column<string>(type: "text", nullable: false),
                    version_parts = table.Column<int[]>(type: "integer[]", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_spt_versions", x => x.version);
                });

            migrationBuilder.CreateTable(
                name: "mod_items",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    prototype = table.Column<string>(type: "text", nullable: true),
                    weight = table.Column<double>(type: "double precision", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    stack_max_size = table.Column<int>(type: "integer", nullable: true),
                    props = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_items", x => new { x.dataset_id, x.id });
                    table.ForeignKey(
                        name: "fk_mod_items_mod_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "mod_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mod_locale_entries",
                columns: table => new
                {
                    scope = table.Column<string>(type: "text", nullable: false),
                    language_code = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: true),
                    field = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_locale_entries", x => new { x.dataset_id, x.scope, x.language_code, x.key });
                    table.ForeignKey(
                        name: "fk_mod_locale_entries_languages_language_code",
                        column: x => x.language_code,
                        principalTable: "languages",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_mod_locale_entries_mod_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "mod_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mod_quests",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quest_name = table.Column<string>(type: "text", nullable: true),
                    trader_id = table.Column<string>(type: "text", nullable: false),
                    location = table.Column<string>(type: "text", nullable: true),
                    type = table.Column<string>(type: "text", nullable: true),
                    side = table.Column<string>(type: "text", nullable: true),
                    restartable = table.Column<bool>(type: "boolean", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_quests", x => new { x.dataset_id, x.id });
                    table.ForeignKey(
                        name: "fk_mod_quests_mod_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "mod_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mod_templates",
                columns: table => new
                {
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    template_id = table.Column<string>(type: "text", nullable: false),
                    op = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_templates", x => new { x.dataset_id, x.kind, x.template_id });
                    table.CheckConstraint("ck_mod_templates_kind", "kind IN ('item', 'quest', 'trader')");
                    table.CheckConstraint("ck_mod_templates_op", "op IN ('added', 'modified', 'removed')");
                    table.ForeignKey(
                        name: "fk_mod_templates_mod_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "mod_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mod_trader_assort_items",
                columns: table => new
                {
                    trader_id = table.Column<string>(type: "text", nullable: false),
                    instance_id = table.Column<string>(type: "text", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<string>(type: "text", nullable: true),
                    slot_id = table.Column<string>(type: "text", nullable: true),
                    upd = table.Column<string>(type: "jsonb", nullable: true),
                    loyalty_level = table.Column<int>(type: "integer", nullable: true),
                    barter_scheme = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_trader_assort_items", x => new { x.dataset_id, x.trader_id, x.instance_id });
                    table.ForeignKey(
                        name: "fk_mod_trader_assort_items_mod_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "mod_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mod_trader_quest_assorts",
                columns: table => new
                {
                    trader_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    assort_instance_id = table.Column<string>(type: "text", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quest_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_trader_quest_assorts", x => new { x.dataset_id, x.trader_id, x.status, x.assort_instance_id });
                    table.ForeignKey(
                        name: "fk_mod_trader_quest_assorts_mod_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "mod_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mod_traders",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    nickname = table.Column<string>(type: "text", nullable: true),
                    currency = table.Column<string>(type: "text", nullable: true),
                    location = table.Column<string>(type: "text", nullable: true),
                    @base = table.Column<string>(name: "base", type: "jsonb", nullable: false),
                    dialogue = table.Column<string>(type: "jsonb", nullable: true),
                    suits = table.Column<string>(type: "jsonb", nullable: true),
                    services = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_traders", x => new { x.dataset_id, x.id });
                    table.ForeignKey(
                        name: "fk_mod_traders_mod_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "mod_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "items",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    spt_version = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    prototype = table.Column<string>(type: "text", nullable: true),
                    weight = table.Column<double>(type: "double precision", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    stack_max_size = table.Column<int>(type: "integer", nullable: true),
                    props = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items", x => new { x.spt_version, x.id });
                    table.ForeignKey(
                        name: "fk_items_spt_versions_spt_version",
                        column: x => x.spt_version,
                        principalTable: "spt_versions",
                        principalColumn: "version",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "locale_entries",
                columns: table => new
                {
                    scope = table.Column<string>(type: "text", nullable: false),
                    language_code = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    spt_version = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: true),
                    field = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_locale_entries", x => new { x.spt_version, x.scope, x.language_code, x.key });
                    table.ForeignKey(
                        name: "fk_locale_entries_languages_language_code",
                        column: x => x.language_code,
                        principalTable: "languages",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_locale_entries_spt_versions_spt_version",
                        column: x => x.spt_version,
                        principalTable: "spt_versions",
                        principalColumn: "version",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mod_data_observations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mod_id = table.Column<Guid>(type: "uuid", nullable: false),
                    spt_version = table.Column<string>(type: "text", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    dependency_set = table.Column<string>(type: "jsonb", nullable: true),
                    dependency_set_hash = table.Column<string>(type: "text", nullable: true),
                    archive_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_data_observations", x => x.id);
                    table.CheckConstraint("ck_mod_data_observations_kind", "kind IN ('archive', 'probe')");
                    table.CheckConstraint("ck_mod_data_observations_outcome", "outcome IN ('archived', 'not_loaded', 'dependency_unresolved', 'outside_declared_range', 'crashed', 'timeout')");
                    table.ForeignKey(
                        name: "fk_mod_data_observations_mod_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "mod_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mod_data_observations_mods_mod_id",
                        column: x => x.mod_id,
                        principalTable: "mods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_mod_data_observations_spt_versions_spt_version",
                        column: x => x.spt_version,
                        principalTable: "spt_versions",
                        principalColumn: "version",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quests",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    spt_version = table.Column<string>(type: "text", nullable: false),
                    quest_name = table.Column<string>(type: "text", nullable: true),
                    trader_id = table.Column<string>(type: "text", nullable: false),
                    location = table.Column<string>(type: "text", nullable: true),
                    type = table.Column<string>(type: "text", nullable: true),
                    side = table.Column<string>(type: "text", nullable: true),
                    restartable = table.Column<bool>(type: "boolean", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quests", x => new { x.spt_version, x.id });
                    table.ForeignKey(
                        name: "fk_quests_spt_versions_spt_version",
                        column: x => x.spt_version,
                        principalTable: "spt_versions",
                        principalColumn: "version",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "traders",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    spt_version = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    nickname = table.Column<string>(type: "text", nullable: true),
                    currency = table.Column<string>(type: "text", nullable: true),
                    location = table.Column<string>(type: "text", nullable: true),
                    @base = table.Column<string>(name: "base", type: "jsonb", nullable: false),
                    dialogue = table.Column<string>(type: "jsonb", nullable: true),
                    suits = table.Column<string>(type: "jsonb", nullable: true),
                    services = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_traders", x => new { x.spt_version, x.id });
                    table.ForeignKey(
                        name: "fk_traders_spt_versions_spt_version",
                        column: x => x.spt_version,
                        principalTable: "spt_versions",
                        principalColumn: "version",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mod_item_changes",
                columns: table => new
                {
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    template_id = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    op = table.Column<string>(type: "text", nullable: false),
                    old_value = table.Column<string>(type: "jsonb", nullable: true),
                    new_value = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_item_changes", x => new { x.dataset_id, x.kind, x.template_id, x.path });
                    table.CheckConstraint("ck_mod_item_changes_kind", "kind = 'item'");
                    table.CheckConstraint("ck_mod_item_changes_op", "op IN ('set', 'remove')");
                    table.ForeignKey(
                        name: "fk_mod_item_changes_mod_templates_dataset_id_kind_template_id",
                        columns: x => new { x.dataset_id, x.kind, x.template_id },
                        principalTable: "mod_templates",
                        principalColumns: new[] { "dataset_id", "kind", "template_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "trader_assort_items",
                columns: table => new
                {
                    trader_id = table.Column<string>(type: "text", nullable: false),
                    instance_id = table.Column<string>(type: "text", nullable: false),
                    spt_version = table.Column<string>(type: "text", nullable: false),
                    template_id = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<string>(type: "text", nullable: true),
                    slot_id = table.Column<string>(type: "text", nullable: true),
                    upd = table.Column<string>(type: "jsonb", nullable: true),
                    loyalty_level = table.Column<int>(type: "integer", nullable: true),
                    barter_scheme = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trader_assort_items", x => new { x.spt_version, x.trader_id, x.instance_id });
                    table.ForeignKey(
                        name: "fk_trader_assort_items_traders_spt_version_trader_id",
                        columns: x => new { x.spt_version, x.trader_id },
                        principalTable: "traders",
                        principalColumns: new[] { "spt_version", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "trader_quest_assorts",
                columns: table => new
                {
                    trader_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    assort_instance_id = table.Column<string>(type: "text", nullable: false),
                    spt_version = table.Column<string>(type: "text", nullable: false),
                    quest_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trader_quest_assorts", x => new { x.spt_version, x.trader_id, x.status, x.assort_instance_id });
                    table.ForeignKey(
                        name: "fk_trader_quest_assorts_traders_spt_version_trader_id",
                        columns: x => new { x.spt_version, x.trader_id },
                        principalTable: "traders",
                        principalColumns: new[] { "spt_version", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_archive_runs_source_hash_spt_version",
                table: "archive_runs",
                columns: new[] { "source_hash", "spt_version" });

            migrationBuilder.CreateIndex(
                name: "ix_items_props",
                table: "items",
                column: "props")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_items_spt_version_name",
                table: "items",
                columns: new[] { "spt_version", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_items_spt_version_parent_id",
                table: "items",
                columns: new[] { "spt_version", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_locale_entries_language_code",
                table: "locale_entries",
                column: "language_code");

            migrationBuilder.CreateIndex(
                name: "ix_locale_entries_spt_version_entity_id_language_code",
                table: "locale_entries",
                columns: new[] { "spt_version", "entity_id", "language_code" });

            migrationBuilder.CreateIndex(
                name: "ix_mod_data_observations_dataset_id",
                table: "mod_data_observations",
                column: "dataset_id");

            migrationBuilder.CreateIndex(
                name: "ix_mod_data_observations_mod_id_spt_version_kind",
                table: "mod_data_observations",
                columns: new[] { "mod_id", "spt_version", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mod_data_observations_spt_version_outcome",
                table: "mod_data_observations",
                columns: new[] { "spt_version", "outcome" });

            migrationBuilder.CreateIndex(
                name: "ix_mod_datasets_mod_guid_content_hash",
                table: "mod_datasets",
                columns: new[] { "mod_guid", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mod_items_dataset_id_name",
                table: "mod_items",
                columns: new[] { "dataset_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_mod_items_dataset_id_parent_id",
                table: "mod_items",
                columns: new[] { "dataset_id", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_mod_items_props",
                table: "mod_items",
                column: "props")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_mod_locale_entries_dataset_id_entity_id_language_code",
                table: "mod_locale_entries",
                columns: new[] { "dataset_id", "entity_id", "language_code" });

            migrationBuilder.CreateIndex(
                name: "ix_mod_locale_entries_language_code",
                table: "mod_locale_entries",
                column: "language_code");

            migrationBuilder.CreateIndex(
                name: "ix_mod_quests_data",
                table: "mod_quests",
                column: "data")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_mod_quests_dataset_id_trader_id",
                table: "mod_quests",
                columns: new[] { "dataset_id", "trader_id" });

            migrationBuilder.CreateIndex(
                name: "ix_mod_templates_dataset_id_op",
                table: "mod_templates",
                columns: new[] { "dataset_id", "op" });

            migrationBuilder.CreateIndex(
                name: "ix_mod_templates_template_id",
                table: "mod_templates",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "ix_mod_trader_assort_items_dataset_id_template_id",
                table: "mod_trader_assort_items",
                columns: new[] { "dataset_id", "template_id" });

            migrationBuilder.CreateIndex(
                name: "ix_mods_mod_guid_version",
                table: "mods",
                columns: new[] { "mod_guid", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quests_data",
                table: "quests",
                column: "data")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_quests_spt_version_location",
                table: "quests",
                columns: new[] { "spt_version", "location" });

            migrationBuilder.CreateIndex(
                name: "ix_quests_spt_version_trader_id",
                table: "quests",
                columns: new[] { "spt_version", "trader_id" });

            migrationBuilder.CreateIndex(
                name: "ix_spt_versions_version_parts",
                table: "spt_versions",
                column: "version_parts",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_trader_assort_items_spt_version_template_id",
                table: "trader_assort_items",
                columns: new[] { "spt_version", "template_id" });

            migrationBuilder.CreateIndex(
                name: "ix_trader_quest_assorts_spt_version_quest_id",
                table: "trader_quest_assorts",
                columns: new[] { "spt_version", "quest_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "archive_runs");

            migrationBuilder.DropTable(
                name: "items");

            migrationBuilder.DropTable(
                name: "locale_entries");

            migrationBuilder.DropTable(
                name: "mod_data_observations");

            migrationBuilder.DropTable(
                name: "mod_item_changes");

            migrationBuilder.DropTable(
                name: "mod_items");

            migrationBuilder.DropTable(
                name: "mod_locale_entries");

            migrationBuilder.DropTable(
                name: "mod_quests");

            migrationBuilder.DropTable(
                name: "mod_trader_assort_items");

            migrationBuilder.DropTable(
                name: "mod_trader_quest_assorts");

            migrationBuilder.DropTable(
                name: "mod_traders");

            migrationBuilder.DropTable(
                name: "quests");

            migrationBuilder.DropTable(
                name: "trader_assort_items");

            migrationBuilder.DropTable(
                name: "trader_quest_assorts");

            migrationBuilder.DropTable(
                name: "mods");

            migrationBuilder.DropTable(
                name: "mod_templates");

            migrationBuilder.DropTable(
                name: "languages");

            migrationBuilder.DropTable(
                name: "traders");

            migrationBuilder.DropTable(
                name: "mod_datasets");

            migrationBuilder.DropTable(
                name: "spt_versions");
        }
    }
}
