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
    public DbSet<LocaleEntry> LocaleEntries => Set<LocaleEntry>();
    public DbSet<Quest> Quests => Set<Quest>();
    public DbSet<Trader> Traders => Set<Trader>();
    public DbSet<TraderAssortItem> TraderAssortItems => Set<TraderAssortItem>();
    public DbSet<TraderQuestAssort> TraderQuestAssorts => Set<TraderQuestAssort>();

    // Data added by mods
    public DbSet<ModItem> ModItems => Set<ModItem>();
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
        b.Entity<LocaleEntry>().HasKey(x => new { x.SptVersion, x.Scope, x.LanguageCode, x.Key });
        b.Entity<Quest>().HasKey(x => new { x.SptVersion, x.Id });
        b.Entity<Trader>().HasKey(x => new { x.SptVersion, x.Id });

        ConfigureItem<Item>(b);
        ConfigureLocaleEntry<LocaleEntry>(b);
        ConfigureQuest<Quest>(b);
        ConfigureTrader<Trader>(b);

        b.Entity<Item>(e =>
        {
            e.HasOne<SptVersion>().WithMany().HasForeignKey(x => x.SptVersion).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SptVersion, x.ParentId });
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
                "outcome IN ('archived', 'not_loaded', 'dependency_unresolved', 'outside_declared_range', 'crashed', 'timeout')"));

            e.ToTable(t => t.HasCheckConstraint(
                "ck_mod_data_observations_kind",
                "kind IN ('archive', 'probe')"));
        });

        // ---- What a dataset changes (ADR-0017) -----------------------------------------------------
        b.Entity<ModTemplate>(e =>
        {
            e.HasKey(x => new { x.DatasetId, x.Kind, x.TemplateId });
            e.HasOne(x => x.Dataset).WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.TemplateId);
            e.HasIndex(x => new { x.DatasetId, x.Op });

            e.ToTable(t => t.HasCheckConstraint(
                "ck_mod_templates_kind",
                "kind IN ('item', 'quest', 'trader')"));

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
        b.Entity<ModLocaleEntry>().HasKey(x => new { x.DatasetId, x.Scope, x.LanguageCode, x.Key });
        b.Entity<ModQuest>().HasKey(x => new { x.DatasetId, x.Id });
        b.Entity<ModTrader>().HasKey(x => new { x.DatasetId, x.Id });

        ConfigureItem<ModItem>(b);
        ConfigureLocaleEntry<ModLocaleEntry>(b);
        ConfigureQuest<ModQuest>(b);
        ConfigureTrader<ModTrader>(b);

        b.Entity<ModItem>(e =>
        {
            e.HasIndex(x => new { x.DatasetId, x.ParentId });
            e.HasIndex(x => new { x.DatasetId, x.Name });
        });

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
