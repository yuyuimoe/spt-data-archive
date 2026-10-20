using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SptArchive.Archiver.Importers;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// The import path against a real Postgres database (ADR-0004 reads the server files directly).
/// The files are a small synthetic database, so the test needs neither the 600 MB server repository
/// nor its Git LFS item file (TD-04).
/// </summary>
public sealed class ImportTests : IDisposable
{
    private const string ItemId = "123456789012345678901234";
    private const string NodeId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string QuestId = "555555555555555555555555";

    // Deliberately not a real release: the tests also run against a database that holds real data.
    private const string ReleaseA = "999.0.1";
    private const string ReleaseB = "999.0.2";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sptarchive-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task An_import_states_the_release_on_every_official_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteServerDatabase(weight: 0.5);

        await using var scope = await ArchiveDatabase.BeginAsync(cancellationToken);
        await ImportAsync(scope, ReleaseA, cancellationToken);

        var items = await scope.Db.Items.Where(item => item.SptVersion == ReleaseA).ToListAsync(cancellationToken);
        var item = Assert.Single(items, row => row.Id == ItemId);

        Assert.Equal("Item A", item.Name);
        Assert.Equal(0.5, item.Weight);
        Assert.Equal(1, item.Width);
        Assert.Equal(2, item.Height);
        Assert.Equal(3, item.StackMaxSize);
        Assert.Equal(NodeId, item.ParentId);

        var trader = Assert.Single(await scope.Db.Traders.Where(row => row.SptVersion == ReleaseA).ToListAsync(cancellationToken));
        Assert.Equal(ReleaseA, trader.SptVersion);
        Assert.Equal("Prapor", trader.Name);

        var assort = Assert.Single(await scope.Db.TraderAssortItems.Where(row => row.SptVersion == ReleaseA).ToListAsync(cancellationToken));
        Assert.Equal(ReleaseA, assort.SptVersion);
        Assert.Equal(ItemId, assort.TemplateId);

        var quest = Assert.Single(await scope.Db.Quests.Where(row => row.SptVersion == ReleaseA).ToListAsync(cancellationToken));
        Assert.Equal(ReleaseA, quest.SptVersion);
        Assert.Equal("A quest", quest.QuestName);

        var name = Assert.Single(
            await scope.Db.LocaleEntries
                .Where(entry => entry.SptVersion == ReleaseA && entry.Key == ItemId + " Name")
                .ToListAsync(cancellationToken));
        Assert.Equal(ReleaseA, name.SptVersion);
        Assert.Equal(ItemId, name.EntityId);
        Assert.Equal("Name", name.Field);
    }

    [Fact]
    public async Task A_new_release_keeps_the_rows_of_the_old_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteServerDatabase(weight: 0.5);

        await using var scope = await ArchiveDatabase.BeginAsync(cancellationToken);
        await ImportAsync(scope, ReleaseA, cancellationToken);

        // The next server release changes one value of the same item.
        WriteServerDatabase(weight: 0.6);
        await ImportAsync(scope, ReleaseB, cancellationToken);

        var releases = await scope.Db.SptVersions.OrderBy(row => row.Version).ToListAsync(cancellationToken);
        Assert.Equal([ReleaseA, ReleaseB], releases.Where(row => row.Version.StartsWith("999.")).Select(row => row.Version));

        var oldItem = Assert.Single(
            await scope.Db.Items.Where(row => row.SptVersion == ReleaseA && row.Id == ItemId).ToListAsync(cancellationToken));
        var newItem = Assert.Single(
            await scope.Db.Items.Where(row => row.SptVersion == ReleaseB && row.Id == ItemId).ToListAsync(cancellationToken));

        Assert.Equal(0.5, oldItem.Weight); // not changed by the newer import
        Assert.Equal(0.6, newItem.Weight);

        Assert.Equal(2, await scope.Db.Items.CountAsync(row => row.Id == ItemId, cancellationToken));
        Assert.Equal(2, await scope.Db.Traders.CountAsync(row => row.Id == "trader1", cancellationToken));
        Assert.Equal(2, await scope.Db.Quests.CountAsync(row => row.Id == QuestId, cancellationToken));
    }

    private async Task ImportAsync(TestScope scope, string version, CancellationToken cancellationToken)
    {
        await SptVersionRegistry.EnsureAsync(scope.Db, version, cancellationToken);

        var context = new ImportContext(scope.Db, _root, version, batchSize: 5000);

        await new LocaleImporter(NullLogger<LocaleImporter>.Instance).ImportAsync(context, cancellationToken);
        await new ItemImporter(NullLogger<ItemImporter>.Instance).ImportAsync(context, cancellationToken);
        await new QuestImporter(NullLogger<QuestImporter>.Instance).ImportAsync(context, cancellationToken);
        await new TraderImporter(NullLogger<TraderImporter>.Instance).ImportAsync(context, cancellationToken);

        await context.FlushAsync(cancellationToken);
    }

    private void WriteServerDatabase(double weight)
    {
        Write("templates/items.json", $$"""
            {
              "{{ItemId}}": {
                "_id": "{{ItemId}}",
                "_name": "Item A",
                "_parent": "{{NodeId}}",
                "_type": "Item",
                "_props": { "Weight": {{weight}}, "Width": 1, "Height": 2, "StackMaxSize": 3 }
              },
              "{{NodeId}}": {
                "_id": "{{NodeId}}",
                "_name": "Node",
                "_parent": "",
                "_type": "Node",
                "_props": {}
              }
            }
            """);

        Write("templates/quests.json", $$"""
            {
              "{{QuestId}}": {
                "QuestName": "A quest",
                "traderId": "trader1",
                "location": "bigmap",
                "type": "Elimination",
                "side": "Pmc",
                "restartable": true
              }
            }
            """);

        Write("locales/languages.json", """{ "en": "English" }""");

        Write("locales/global/en.json", $$"""
            {
              "{{ItemId}} Name": "Item A",
              "{{ItemId}} ShortName": "A",
              "Voip/DisabledForOffline": "Disabled"
            }
            """);

        Write("traders/trader1/base.json", """
            { "name": "Prapor", "nickname": "P", "currency": "RUB", "location": "bigmap" }
            """);

        Write("traders/trader1/assort.json", $$"""
            {
              "items": [
                { "_id": "offer1", "_tpl": "{{ItemId}}", "parentId": "hideout", "slotId": "hideout",
                  "upd": { "StackObjectsCount": 5 } }
              ],
              "barter_scheme": { "offer1": [[{ "count": 1, "_tpl": "{{ItemId}}" }]] },
              "loyal_level_items": { "offer1": 1 }
            }
            """);

        Write("traders/trader1/questassort.json", $$"""
            { "started": { "offer1": "{{QuestId}}" } }
            """);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
