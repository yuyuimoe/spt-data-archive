using System.Text;
using SptArchive.Archiver.Importers;

namespace SptArchive.Tests;

/// <summary>
/// The JSON reader. Mod authors ship files that the server reads, so the reader must survive what
/// those files contain. 13 of the 392 JSON files in the unpacked mods start with a UTF-8 BOM.
/// </summary>
public sealed class ImportSupportTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sptarchive-support-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_file_with_a_UTF8_BOM_is_read()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "with-bom.json");
        byte[] body = Encoding.UTF8.GetBytes("""{"a":1}""");
        await File.WriteAllBytesAsync(
            path,
            [0xEF, 0xBB, 0xBF, .. body],
            TestContext.Current.CancellationToken);

        using var document = await SourceFiles.ReadJsonAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(1, document.RootElement.GetProperty("a").GetInt32());
    }
}
