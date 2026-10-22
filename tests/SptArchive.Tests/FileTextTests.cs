using System.Text;
using System.Text.Json;
using SptArchive.Api.GraphQL;
using SptArchive.Core.Entities;

namespace SptArchive.Tests;

/// <summary>
/// The text of a configuration file is a guarded field (ADR-0040, amendment 2, TD-59). The manifest states
/// the facts of a file; the text and the document are read as fields of their own, and a page has a budget of
/// bytes, so one mod with a large shipped configuration cannot turn a page of files into a 400 MB answer.
/// </summary>
public sealed class FileTextTests
{
    private const string ModGuid = "com.example.files";

    private static readonly string DatasetId = Guid.NewGuid().ToString();

    private const string ConfigText = """{"Loot":2,"Comment":"a value that a mod read"}""";

    [Fact]
    public async Task The_text_and_the_document_of_a_file_are_served_when_they_fit()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("com.example.files/config/config.json", ModFileKind.Config, ConfigText));

        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modFiles(first: 10, where: { datasetId: { eq: "{{DatasetId}}" } }) { nodes { path kind contentText content } } }""");

        Assert.False(response.TryGetProperty("errors", out var errors), errors.ToString());
        var row = ApiHarness.Nodes(response, "modFiles").EnumerateArray().Single();
        Assert.Equal(ConfigText, row.GetProperty("contentText").GetString());
        Assert.Equal(2, row.GetProperty("content").GetProperty("Loot").GetInt32());
    }

    [Fact]
    public async Task A_file_that_is_not_a_configuration_carries_no_text()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("com.example.files/BepInEx/plugins/mod.dll", ModFileKind.Assembly, null));

        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modFiles(first: 10, where: { datasetId: { eq: "{{DatasetId}}" } }) { nodes { path kind contentText content } } }""");

        Assert.False(response.TryGetProperty("errors", out var errors), errors.ToString());
        var row = ApiHarness.Nodes(response, "modFiles").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, row.GetProperty("contentText").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task A_text_above_the_limit_is_refused_and_names_its_size()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("com.example.files/config/config.json", ModFileKind.Config, ConfigText));

        // The text is read through a field that pays for its bytes, so a limit of ten bytes refuses it and
        // the client learns both numbers instead of receiving an empty answer.
        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modFiles(first: 10, where: { datasetId: { eq: "{{DatasetId}}" } }) { nodes { path contentText } } }""",
            fileText: limits => limits.MaxValueBytes = 10);

        var (code, message) = ApiHarness.Error(response);
        Assert.Equal(FileTextErrors.ValueTooLargeCode, code);
        Assert.Contains(Encoding.UTF8.GetByteCount(ConfigText).ToString(), message);
        Assert.Contains("10", message);
    }

    [Fact]
    public async Task A_page_of_texts_above_the_budget_is_refused_after_the_first_one()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(
            scope,
            ("com.example.files/config/one.json", ModFileKind.Config, """{"a":1,"pad":"aaaaaaaaaa"}"""),
            ("com.example.files/config/two.json", ModFileKind.Config, """{"b":2,"pad":"bbbbbbbbbb"}"""));

        // Each text is about 27 bytes and the request may carry 40, so one text is served and the second is
        // refused. The cost analysis allows the query: it bounds the shape of a request, and this bounds the
        // bytes that come out of it.
        var response = await ApiHarness.JsonAsync(
            scope,
            $$"""{ modFiles(first: 10, where: { datasetId: { eq: "{{DatasetId}}" } }) { nodes { path contentText } } }""",
            fileText: limits =>
            {
                limits.MaxValueBytes = 1_000;
                limits.MaxRequestBytes = 40;
            });

        var (code, _) = ApiHarness.Error(response);
        Assert.Equal(FileTextErrors.RequestLimitCode, code);
    }

    [Fact]
    public async Task A_row_pays_for_its_bytes_once_whatever_field_reads_them()
    {
        await using var scope = await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);
        await SeedAsync(scope, ("com.example.files/config/config.json", ModFileKind.Config, ConfigText));

        // The text and the document come from the same stored bytes, so a budget that holds one file pays
        // once even when the client asks for both fields.
        var response = await ApiHarness.JsonAsync(
            scope,
            """{ modFiles(first: 10) { nodes { path contentText content } } }""",
            fileText: limits =>
            {
                limits.MaxValueBytes = 1_000;
                limits.MaxRequestBytes = Encoding.UTF8.GetByteCount(ConfigText);
            });

        Assert.False(response.TryGetProperty("errors", out var errors), errors.ToString());
        var row = ApiHarness.Nodes(response, "modFiles").EnumerateArray().Single();
        Assert.Equal(ConfigText, row.GetProperty("contentText").GetString());
        Assert.Equal(2, row.GetProperty("content").GetProperty("Loot").GetInt32());
    }

    private static async Task SeedAsync(TestScope scope, params (string Path, string Kind, string? Text)[] files)
    {
        var token = TestContext.Current.CancellationToken;
        var dataset = new ModDataset
        {
            Id = Guid.Parse(DatasetId),
            ModGuid = ModGuid,
            ContentHash = "sha256:files",
            TableHashes = """{"files":"sha256:files"}""",
            RowCounts = """{"files":1}""",
        };

        scope.Db.ModDatasets.Add(dataset);

        foreach (var (path, kind, text) in files)
        {
            scope.Db.ModFiles.Add(new ModFile
            {
                DatasetId = dataset.Id,
                Path = path,
                Kind = kind,
                Size = Encoding.UTF8.GetByteCount(text ?? """{"placeholder":"an assembly of bytes"}"""),
                Sha256 = "sha256:" + Path.GetFileName(path),
                ContentText = text,
                Content = text,
            });
        }

        await scope.Db.SaveChangesAsync(token);
    }
}
