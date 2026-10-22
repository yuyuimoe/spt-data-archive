using Microsoft.EntityFrameworkCore;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// The four kind checks name every kind the archive writes (ADR-0044). A row is stored and read back, because a
/// row that is only built in memory proves nothing about a CHECK constraint.
/// </summary>
public sealed class KindCheckTests
{
    private static readonly string[] NewKinds =
    [
        ModTemplateKind.Dialogue, ModTemplateKind.Preset, ModTemplateKind.BotPreset, ModTemplateKind.Scattering,
        ModTemplateKind.Global, ModTemplateKind.Setting, ModTemplateKind.Profile, ModTemplateKind.Achievement,
        ModTemplateKind.Storage, ModTemplateKind.Character,
    ];

    [Fact]
    public async Task Every_kind_of_the_seven_files_is_stored_and_read_back_in_all_four_tables()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = await ArchiveDatabase.BeginAsync(token);

        var dataset = new ModDataset
        {
            ModGuid = "com.example.kinds",
            ContentHash = "sha256:kinds",
            TableHashes = "{}",
            RowCounts = "{}",
        };

        scope.Db.ModDatasets.Add(dataset);

        foreach (var kind in NewKinds)
        {
            scope.Db.ModTemplates.Add(new ModTemplate { DatasetId = dataset.Id, Kind = kind, TemplateId = "x", Op = ModTemplateOperation.Modified });
            scope.Db.ModChangePaths.Add(new ModChangePath { DatasetId = dataset.Id, Kind = kind, TemplateId = "x", Path = "/a", Op = ModChangePathOperation.Set, OldValue = "1", NewValue = "2" });
            scope.Db.ModIdMaps.Add(new ModIdMap { DatasetId = dataset.Id, Kind = kind, RawId = "x", Address = "y" });
            scope.Db.ModDatabaseObjects.Add(new ModDatabaseObject { DatasetId = dataset.Id, Kind = kind, TemplateId = "x", Data = "{\"a\":1}", Bytes = 7 });
        }

        await scope.Db.SaveChangesAsync(token);
        scope.Db.ChangeTracker.Clear();

        foreach (var kind in NewKinds)
        {
            Assert.Equal("2", (await scope.Db.ModChangePaths.SingleAsync(x => x.DatasetId == dataset.Id && x.Kind == kind, token)).NewValue);
            Assert.Equal(7, (await scope.Db.ModDatabaseObjects.SingleAsync(x => x.DatasetId == dataset.Id && x.Kind == kind, token)).Bytes);
            Assert.Equal("y", (await scope.Db.ModIdMaps.SingleAsync(x => x.DatasetId == dataset.Id && x.Kind == kind, token)).Address);
            Assert.Equal(ModTemplateOperation.Modified, (await scope.Db.ModTemplates.SingleAsync(x => x.DatasetId == dataset.Id && x.Kind == kind, token)).Op);
        }
    }

    [Fact]
    public async Task A_kind_that_no_reader_writes_is_still_refused()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = await ArchiveDatabase.BeginAsync(token);

        var dataset = new ModDataset { ModGuid = "com.example.kinds", ContentHash = "sha256:refused", TableHashes = "{}", RowCounts = "{}" };
        scope.Db.ModDatasets.Add(dataset);
        scope.Db.ModTemplates.Add(new ModTemplate { DatasetId = dataset.Id, Kind = "unknown", TemplateId = "x", Op = ModTemplateOperation.Added });

        await Assert.ThrowsAsync<DbUpdateException>(() => scope.Db.SaveChangesAsync(token));
    }
}
