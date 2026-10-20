using Microsoft.EntityFrameworkCore;
using SptArchive.Core.Entities;

namespace SptArchive.Core.Data;

/// <summary>
/// The archive schema. Official data and mod data live in parallel tables (<c>items</c> / <c>mod_items</c>,
/// <c>quests</c> / <c>mod_quests</c>, ...) that share the same columns.
/// </summary>
/// <remarks>
/// <para>
/// Every official row belongs to one server release and states it in <c>spt_version</c>, which is the
/// first column of every official primary key (ADR-0014). The archive keeps every release.
/// </para>
/// <para>
/// Every <c>mod_*</c> row belongs to one mod dataset, not to a mod version, so two versions with the
/// same content store their rows once (ADR-0013). A dataset is observed on one or more server releases.
/// </para>
/// <para>Table and column names are snake_case (EFCore.NamingConventions).</para>
/// </remarks>
public class ArchiveDbContext(DbContextOptions<ArchiveDbContext> options) : DbContext(options)
{
    // Bookkeeping
    public DbSet<Mod> Mods => Set<Mod>();
    public DbSet<Language> Languages => Set<Language>();
    public DbSet<ArchiveRun> ArchiveRuns => Set<ArchiveRun>();
    public DbSet<SptVersion> SptVersions => Set<SptVersion>();

    // Official data (comes straight from the server's own database files)
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Customization> Customizations => Set<Customization>();
    public DbSet<LocaleEntry> LocaleEntries => Set<LocaleEntry>();
    public DbSet<Quest> Quests => Set<Quest>();
    public DbSet<Trader> Traders => Set<Trader>();
    public DbSet<TraderAssortItem> TraderAssortItems => Set<TraderAssortItem>();
    public DbSet<TraderQuestAssort> TraderQuestAssorts => Set<TraderQuestAssort>();

    // Data added by mods
    public DbSet<ModItem> ModItems => Set<ModItem>();
    public DbSet<ModCustomization> ModCustomizations => Set<ModCustomization>();
    public DbSet<ModLocaleEntry> ModLocaleEntries => Set<ModLocaleEntry>();
    public DbSet<ModQuest> ModQuests => Set<ModQuest>();
    public DbSet<ModTrader> ModTraders => Set<ModTrader>();
    public DbSet<ModTraderAssortItem> ModTraderAssortItems => Set<ModTraderAssortItem>();
    public DbSet<ModTraderQuestAssort> ModTraderQuestAssorts => Set<ModTraderQuestAssort>();

    // What a mod dataset contains, and what it changes in official data
    public DbSet<ModDataset> ModDatasets => Set<ModDataset>();
    public DbSet<ModDataObservation> ModDataObservations => Set<ModDataObservation>();
    public DbSet<ModTemplate> ModTemplates => Set<ModTemplate>();
    public DbSet<ModItemChange> ModItemChanges => Set<ModItemChange>();

    // The files a dataset loaded: the manifest of every mod, and the text of its configuration (ADR-0029)
    public DbSet<ModFile> ModFiles => Set<ModFile>();

    // The id that a run gave an object and the address the archive derived for it (ADR-0034)
    public DbSet<ModIdMap> ModIdMaps => Set<ModIdMap>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ---- Server releases and runs -------------------------------------------------------------
        b.Entity<SptVersion>(e =>
        {
            e.HasKey(x => x.Version);

            // "Latest" must never be ambiguous, and two keys with the same numbers are one release.
            e.HasIndex(x => x.VersionParts).IsUnique();
        });

        b.Entity<Language>().HasKey(x => x.Code);

        b.Entity<Mod>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ModGuid, x.Version }).IsUnique();
            e.Property(x => x.Metadata).HasColumnType("jsonb");
        });

        b.Entity<ArchiveRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SourceHash, x.SptVersion });
        });

        // ---- Official data: one row set per server release (ADR-0014) ------------------------------
        b.Entity<Item>().HasKey(x => new { x.SptVersion, x.Id });
        b.Entity<Customization>().HasKey(x => new { x.SptVersion, x.Id });
        b.Entity<LocaleEntry>().HasKey(x => new { x.SptVersion, x.Scope, x.LanguageCode, x.Key });
        b.Entity<Quest>().HasKey(x => new { x.SptVersion, x.Id });
        b.Entity<Trader>().HasKey(x => new { x.SptVersion, x.Id });

        ConfigureItem<Item>(b);
        ConfigureCustomization<Customization>(b);
        ConfigureLocaleEntry<LocaleEntry>(b);
        ConfigureQuest<Quest>(b);
        ConfigureTrader<Trader>(b);

        b.Entity<Item>(e =>
        {
            e.HasOne<SptVersion>().WithMany().HasForeignKey(x => x.SptVersion).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SptVersion, x.ParentId });
            e.HasIndex(x => new { x.SptVersion, x.Name });
        });

        b.Entity<Customization>(e =>
        {
            e.HasOne<SptVersion>().WithMany().HasForeignKey(x => x.SptVersion).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SptVersion, x.BodyPart });
            e.HasIndex(x => new { x.SptVersion, x.Name });
        });

        b.Entity<LocaleEntry>(e =>
        {
            e.HasOne<SptVersion>().WithMany().HasForeignKey(x => x.SptVersion).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SptVersion, x.EntityId, x.LanguageCode });
        });

        b.Entity<Quest>(e =>
        {
            e.HasOne<SptVersion>().WithMany().HasForeignKey(x => x.SptVersion).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SptVersion, x.TraderId });
            e.HasIndex(x => new { x.SptVersion, x.Location });
        });

        b.Entity<Trader>(e =>
            e.HasOne<SptVersion>().WithMany().HasForeignKey(x => x.SptVersion).OnDelete(DeleteBehavior.Restrict));

        // Assort rows belong to the trader of their own release, so deleting a release removes its stock.
        b.Entity<TraderAssortItem>(e =>
        {
            e.HasKey(x => new { x.SptVersion, x.TraderId, x.InstanceId });
            e.HasOne<Trader>().WithMany()
                .HasForeignKey(x => new { x.SptVersion, x.TraderId })
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SptVersion, x.TemplateId });
        });
        ConfigureAssortItem<TraderAssortItem>(b);

        b.Entity<TraderQuestAssort>(e =>
        {
            e.HasKey(x => new { x.SptVersion, x.TraderId, x.Status, x.AssortInstanceId });
            e.HasOne<Trader>().WithMany()
                .HasForeignKey(x => new { x.SptVersion, x.TraderId })
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SptVersion, x.QuestId });
        });

        // ---- Mod datasets and observations (ADR-0013) ----------------------------------------------
        b.Entity<ModDataset>(e =>
        {
            e.HasKey(x => x.Id);

            // Two versions of one mod with the same content share one dataset.
            e.HasIndex(x => new { x.ModGuid, x.ContentHash }).IsUnique();
            e.Property(x => x.TableHashes).HasColumnType("jsonb");
            e.Property(x => x.RowCounts).HasColumnType("jsonb");

            e.ToTable(t => t.HasCheckConstraint(
                "ck_mod_datasets_config_origin",
                "config_origin IN ('shipped', 'curated')"));
        });

        b.Entity<ModDataObservation>(e =>
        {
            e.HasKey(x => x.Id);

            e.HasOne(x => x.Mod).WithMany().HasForeignKey(x => x.ModId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Dataset).WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SptVersion>().WithMany().HasForeignKey(x => x.SptVersion).OnDelete(DeleteBehavior.Restrict);

            // An archive observation and a probe observation are different facts about the same pair.
            e.HasIndex(x => new { x.ModId, x.SptVersion, x.Kind }).IsUnique();
            e.HasIndex(x => new { x.SptVersion, x.Outcome });
            e.HasIndex(x => x.DatasetId);

            e.Property(x => x.DependencySet).HasColumnType("jsonb");

            e.ToTable(t => t.HasCheckConstraint(
                "ck_mod_data_observations_outcome",
                "outcome IN ('archived', 'not_loaded', 'dependency_unresolved', 'outside_declared_range', 'crashed', 'timeout', 'no_server_data', 'loaded_but_disabled')"));

            e.ToTable(t => t.HasCheckConstraint(
                "ck_mod_data_observations_kind",
                "kind IN ('archive', 'probe')"));
        });

        // ---- The files a dataset loaded (ADR-0029) -------------------------------------------------
        b.Entity<ModFile>(e =>
        {
            // The path starts with the mod directory, so one dataset can hold a target and its deps.
            e.HasKey(x => new { x.DatasetId, x.Path });
            e.HasOne(x => x.Dataset).WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Cascade);

            e.Property(x => x.Content).HasColumnType("jsonb");

            // "Which datasets ship this file" is a lookup on the hash, not on the path.
            e.HasIndex(x => x.Sha256);
            e.HasIndex(x => new { x.DatasetId, x.Kind });

            e.ToTable(t => t.HasCheckConstraint(
                "ck_mod_files_kind",
                "kind IN ('assembly', 'bundle', 'data', 'config', 'text', 'other')"));
        });

        // ---- What a dataset changes (ADR-0017) -----------------------------------------------------
        b.Entity<ModIdMap>(e =>
        {
            // One row per id of one dataset; the id is unique inside its kind, so the kind is part of the key.
            e.HasKey(x => new { x.DatasetId, x.Kind, x.RawId });
            e.HasOne(x => x.Dataset).WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Cascade);

            // "Which object is this id" is a lookup by the id; "which ids does this address name" by the address.
            e.HasIndex(x => new { x.Kind, x.RawId });
            e.HasIndex(x => new { x.DatasetId, x.Address });

            e.ToTable(t => t.HasCheckConstraint(
                "ck_mod_id_map_kind",
                "kind IN ('item', 'customization', 'quest', 'trader')"));
        });

        b.Entity<ModTemplate>(e =>
        {
            e.HasKey(x => new { x.DatasetId, x.Kind, x.TemplateId });
            e.HasOne(x => x.Dataset).WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.TemplateId);
            e.HasIndex(x => new { x.DatasetId, x.Op });

            e.ToTable(t => t.HasCheckConstraint(
                "ck_mod_templates_kind",
                "kind IN ('item', 'customization', 'quest', 'trader')"));

            e.ToTable(t => t.HasCheckConstraint(
                "ck_mod_templates_op",
                "op IN ('added', 'modified', 'removed')"));
        });

        b.Entity<ModItemChange>(e =>
        {
            e.HasKey(x => new { x.DatasetId, x.Kind, x.TemplateId, x.Path });

            e.HasOne(x => x.Template).WithMany()
                .HasForeignKey(x => new { x.DatasetId, x.Kind, x.TemplateId })
                .OnDelete(DeleteBehavior.Cascade);

            e.Property(x => x.OldValue).HasColumnType("jsonb");
            e.Property(x => x.NewValue).HasColumnType("jsonb");

            e.ToTable(t => t.HasCheckConstraint("ck_mod_item_changes_kind", "kind = 'item'"));
            e.ToTable(t => t.HasCheckConstraint("ck_mod_item_changes_op", "op IN ('set', 'remove')"));
        });

        // ---- Mod data: one row set per dataset ------------------------------------------------------
        b.Entity<ModItem>().HasKey(x => new { x.DatasetId, x.Id });
        b.Entity<ModCustomization>().HasKey(x => new { x.DatasetId, x.Id });
        b.Entity<ModLocaleEntry>().HasKey(x => new { x.DatasetId, x.Scope, x.LanguageCode, x.Key });
        b.Entity<ModQuest>().HasKey(x => new { x.DatasetId, x.Id });
        b.Entity<ModTrader>().HasKey(x => new { x.DatasetId, x.Id });

        ConfigureItem<ModItem>(b);
        ConfigureCustomization<ModCustomization>(b);
        ConfigureLocaleEntry<ModLocaleEntry>(b);
        ConfigureQuest<ModQuest>(b);
        ConfigureTrader<ModTrader>(b);

        b.Entity<ModItem>(e =>
        {
            e.HasIndex(x => new { x.DatasetId, x.ParentId });
            e.HasIndex(x => new { x.DatasetId, x.Name });
        });

        b.Entity<ModCustomization>(e => e.HasIndex(x => new { x.DatasetId, x.BodyPart }));
        b.Entity<ModLocaleEntry>(e => e.HasIndex(x => new { x.DatasetId, x.EntityId, x.LanguageCode }));
        b.Entity<ModQuest>(e => e.HasIndex(x => new { x.DatasetId, x.TraderId }));

        // Mod assort rows have no trader foreign key: they may target an official or a modded trader.
        b.Entity<ModTraderAssortItem>(e =>
        {
            e.HasKey(x => new { x.DatasetId, x.TraderId, x.InstanceId });
            e.HasIndex(x => new { x.DatasetId, x.TemplateId });
        });
        ConfigureAssortItem<ModTraderAssortItem>(b);

        b.Entity<ModTraderQuestAssort>(e => e.HasKey(x => new { x.DatasetId, x.TraderId, x.Status, x.AssortInstanceId }));

        // ---- Every mod-owned table: foreign key to mod_datasets --------------------------------------
        b.Entity<ModItem>().HasOne(x => x.Dataset).WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ModCustomization>().HasOne(x => x.Dataset).WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ModLocaleEntry>().HasOne(x => x.Dataset).WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ModQuest>().HasOne(x => x.Dataset).WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ModTrader>().HasOne(x => x.Dataset).WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ModTraderAssortItem>().HasOne(x => x.Dataset).WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ModTraderQuestAssort>().HasOne(x => x.Dataset).WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureItem<T>(ModelBuilder b) where T : ItemBase =>
        b.Entity<T>(e =>
        {
            e.Property(x => x.Props).HasColumnType("jsonb");

            // Containment queries on the raw props ("Props @> '{...}'") without a column per property.
            e.HasIndex(x => x.Props).HasMethod("gin").HasOperators("jsonb_path_ops");
        });

    private static void ConfigureCustomization<T>(ModelBuilder b) where T : CustomizationBase =>
        b.Entity<T>(e =>
        {
            e.Property(x => x.Sides).HasColumnType("text[]");
            e.Property(x => x.Props).HasColumnType("jsonb");

            // "Which clothes may this side wear?" without scanning every row.
            e.HasIndex(x => x.Sides).HasMethod("gin");
            e.HasIndex(x => x.Props).HasMethod("gin").HasOperators("jsonb_path_ops");
        });

    private static void ConfigureLocaleEntry<T>(ModelBuilder b) where T : LocaleEntryBase =>
        b.Entity<T>(e => e.HasOne<Language>().WithMany().HasForeignKey(x => x.LanguageCode));

    private static void ConfigureQuest<T>(ModelBuilder b) where T : QuestBase =>
        b.Entity<T>(e =>
        {
            e.Property(x => x.Data).HasColumnType("jsonb");
            e.HasIndex(x => x.Data).HasMethod("gin").HasOperators("jsonb_path_ops");
        });

    private static void ConfigureTrader<T>(ModelBuilder b) where T : TraderBase =>
        b.Entity<T>(e =>
        {
            e.Property(x => x.Base).HasColumnType("jsonb");
            e.Property(x => x.Dialogue).HasColumnType("jsonb");
            e.Property(x => x.Suits).HasColumnType("jsonb");
            e.Property(x => x.Services).HasColumnType("jsonb");
        });

    private static void ConfigureAssortItem<T>(ModelBuilder b) where T : AssortItemBase =>
        b.Entity<T>(e =>
        {
            e.Property(x => x.Upd).HasColumnType("jsonb");
            e.Property(x => x.BarterScheme).HasColumnType("jsonb");
        });
}
