using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Api.GraphQL;

/// <summary>
/// The most bytes of configuration text that one request may carry (ADR-0040, amendment 2).
/// </summary>
/// <remarks>
/// A run stores the text of a configuration file, because the text explains the run (ADR-0029), and one file
/// may hold 2 MB. A page of two hundred files would then be 400 MB, which the Api builds and the cache
/// refuses to store. The cost analysis bounds the shape of a request and not its bytes, so these two numbers
/// bound the bytes.
/// </remarks>
public sealed class FileTextOptions
{
    public const string Section = "FileText";

    /// <summary>
    /// The largest text, in bytes, that one field returns. It is the cap that the import applies to one file,
    /// so a text that the archive holds is served and nothing larger is.
    /// </summary>
    public long MaxValueBytes { get; set; } = 2_000_000;

    /// <summary>The most bytes of configuration text that one request returns, across every row of every page.</summary>
    public long MaxRequestBytes { get; set; } = 4_000_000;
}

/// <summary>The codes of configuration text that the Api refuses to serve.</summary>
public static class FileTextErrors
{
    /// <summary>The text is larger than <see cref="FileTextOptions.MaxValueBytes" />.</summary>
    public const string ValueTooLargeCode = "CONTENT_TEXT_TOO_LARGE";

    /// <summary>The request already carried <see cref="FileTextOptions.MaxRequestBytes" /> of text.</summary>
    public const string RequestLimitCode = "CONTENT_TEXT_REQUEST_LIMIT";

    public static GraphQLException Fail(string code, string message) =>
        new(ErrorBuilder.New().SetMessage(message).SetCode(code).Build());
}

/// <summary>
/// Counts the bytes of configuration text that one request carries, and refuses the one that passes a limit.
/// </summary>
/// <remarks>
/// The text and the document of a file come from the same stored bytes, so a row pays once whatever field
/// reads it: asking for both costs the row's size one time. A row that holds no text pays nothing, and the
/// check costs no query: it reads the size and the kind of the row, which are columns, and never the text.
/// It lives in the global state of one operation (as the budget of whole documents does), so two clients never
/// share it and every field of one request pays into the same total.
/// </remarks>
public sealed class FileTextBudget(FileTextOptions options)
{
    /// <summary>The key of the budget in the state of the operation.</summary>
    public const string StateKey = "spt:file-text-budget";

    private readonly HashSet<(Guid DatasetId, string Path)> _charged = [];

    private long _spent;

    /// <summary>
    /// A configuration file carries text; the manifest entry of any other kind never does (ADR-0029), so only
    /// a configuration row is counted.
    /// </summary>
    public static bool CarriesText(ModFile row) =>
        string.Equals(row.Kind, ModFileKind.Config, StringComparison.Ordinal);

    /// <summary>
    /// Refuses a text before anything reads it, and charges the request for it. The size of the text is the
    /// size of the file on disk, which is an upper bound: it never undercounts the bytes that come out.
    /// </summary>
    public void Charge(ModFile row)
    {
        if (!CarriesText(row) || !_charged.Add((row.DatasetId, row.Path)))
        {
            return;
        }

        if (row.Size > options.MaxValueBytes)
        {
            throw FileTextErrors.Fail(
                FileTextErrors.ValueTooLargeCode,
                $"The configuration file '{row.Path}' is {row.Size} bytes, and the largest text that one field returns is {options.MaxValueBytes}. Read its path, its kind, its size and its SHA-256, or ask for the file through the manifest.");
        }

        var spent = Interlocked.Add(ref _spent, row.Size);
        if (spent > options.MaxRequestBytes)
        {
            throw FileTextErrors.Fail(
                FileTextErrors.RequestLimitCode,
                $"This request passes the {options.MaxRequestBytes} bytes of configuration text that one request may carry: it already served {spent} bytes. Ask for fewer files, or for the small fields of each.");
        }
    }
}

/// <summary>
/// The files of a dataset. The manifest states the path, the kind, the size and the hash of each file; the
/// text of a configuration file and the document that it parses to are two guarded fields, because a page of
/// texts is large (ADR-0029, ADR-0040 amendment 2).
/// </summary>
public sealed class ModFileJson : ObjectTypeExtension<ModFile>
{
    protected override void Configure(IObjectTypeDescriptor<ModFile> d)
    {
        // The key and the size of the row are read with every row: the resolver of the two guarded fields
        // needs them to charge the request, and it never reads the text of a row that it did not charge.
        d.Field(x => x.DatasetId).IsProjected(true);
        d.Field(x => x.Path).IsProjected(true);
        d.Field(x => x.Size).IsProjected(true);
        d.Field(x => x.Kind).IsProjected(true);

        // The stored text and document are not projected: a resolver reads them, with the limit in the
        // statement, so a page above the limit is refused before anything is assembled in memory.
        d.Field(x => x.ContentText)
            .IsProjected(false)
            .Cost(GraphQLSetup.FileTextCost)
            .Description($"The text of a configuration file, as the mod read it. A page of texts is bounded by FileText:MaxRequestBytes, and one text by FileText:MaxValueBytes, which answers {FileTextErrors.ValueTooLargeCode} and {FileTextErrors.RequestLimitCode}.")
            .Resolve(context => ResolveAsync(context, text: true, context.RequestAborted));

        d.Field(x => x.Content)
            .Type<AnyType>()
            .IsProjected(false)
            .Cost(GraphQLSetup.FileTextCost)
            .Description($"The stored text of a configuration file, as JSON, when it parses. It shares the budget of the text: raw text, a file that is not JSON, or a page above the limit answers {FileTextErrors.ValueTooLargeCode} or {FileTextErrors.RequestLimitCode}.")
            .Resolve(context => ResolveAsync(context, text: false, context.RequestAborted));
    }

    private static async Task<object?> ResolveAsync(IResolverContext context, bool text, CancellationToken cancellationToken)
    {
        var row = context.Parent<ModFile>();
        var options = context.Service<IOptions<FileTextOptions>>().Value;

        // One budget for the whole operation, made the first time a field asks for it. Before the read,
        // because the read is what costs.
        var budget = context.GetOrSetGlobalState(FileTextBudget.StateKey, _ => new FileTextBudget(options));
        budget.Charge(row);

        if (!FileTextBudget.CarriesText(row))
        {
            return null;
        }

        // A context of its own, because the fields of one page resolve at the same time and a DbContext is not
        // safe to use twice at once. The limit goes into the statement, so a text above it is never assembled.
        var factory = context.Service<IDbContextFactory<ArchiveDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var stored = await db.ModFiles
            .Where(x => x.DatasetId == row.DatasetId && x.Path == row.Path
                && x.Kind == ModFileKind.Config && x.Size <= options.MaxValueBytes)
            .Select(x => new { x.ContentText, x.Content })
            .FirstOrDefaultAsync(cancellationToken);

        if (stored is null)
        {
            return null;
        }

        return text ? stored.ContentText : JsonText.Parse(stored.Content);
    }
}
