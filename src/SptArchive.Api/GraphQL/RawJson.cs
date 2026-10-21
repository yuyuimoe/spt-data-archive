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
/// The most of a whole document that one request may carry, in bytes (ADR-0040, decision 8).
/// </summary>
/// <remarks>
/// A client reads a typed field for the small facts and the whole document only when it asks for it, and a
/// document is large: the loose loot of one map is 132 MB on disk and 175 MB in memory, and reading it costs
/// 138 ms (measured on 2026-10-21, ADR-0041). So the whole document is a field of its own, it costs a
/// hundred times a small field in the cost analysis, and these two numbers bound the bytes that come out.
/// </remarks>
public sealed class RawJsonOptions
{
    public const string Section = "RawJson";

    /// <summary>
    /// The largest document, in bytes, that one field returns. It equals <c>Cache:MaxResponseBytes</c>, so a
    /// document that the Api refuses to cache is a document that it refuses to build.
    /// </summary>
    public long MaxValueBytes { get; set; } = 2_000_000;

    /// <summary>
    /// The most bytes of whole documents that one request returns, across every field of every row. A page of
    /// two hundred rows of two megabytes would be 400 MB, which neither the cache nor a client wants.
    /// </summary>
    public long MaxRequestBytes { get; set; } = 4_000_000;
}

/// <summary>The codes of a whole document that the Api refuses to serve.</summary>
public static class RawJsonErrors
{
    /// <summary>The document is larger than <see cref="RawJsonOptions.MaxValueBytes" />.</summary>
    public const string ValueTooLargeCode = "RAW_JSON_TOO_LARGE";

    /// <summary>The request already carried <see cref="RawJsonOptions.MaxRequestBytes" /> of whole documents.</summary>
    public const string RequestLimitCode = "RAW_JSON_REQUEST_LIMIT";

    public static GraphQLException Fail(string code, string message) =>
        new(ErrorBuilder.New().SetMessage(message).SetCode(code).Build());
}

/// <summary>
/// Counts the bytes of whole documents that one request carries, and refuses the one that passes a limit.
/// </summary>
/// <remarks>
/// The cost analysis already refuses a query that asks for a whole page of whole documents, because it
/// bounds the shape of a request and not its data. This bounds the data. It lives in the global state of
/// one operation, so two clients never share it and every field of one request pays into the same total,
/// and the check costs no query: it reads the size of the row, which is a column, and never the document.
/// </remarks>
public sealed class RawJsonBudget(RawJsonOptions options)
{
    /// <summary>The key of the budget in the state of the operation.</summary>
    public const string StateKey = "spt:raw-json-budget";

    private long _spent;

    /// <summary>
    /// Refuses a document before anything reads it, and charges the request for it. A refusal comes first,
    /// because the read is what costs: PostgreSQL keeps a value this large outside the row and compressed, and
    /// assembling one costs 138 ms and 175 MB for the 132 MB loose loot of lighthouse.
    /// </summary>
    public void Charge(ModDatabaseObject row)
    {
        if (row.Bytes > options.MaxValueBytes)
        {
            throw RawJsonErrors.Fail(
                RawJsonErrors.ValueTooLargeCode,
                $"The object {row.Kind} {row.TemplateId} in part '{row.Part}' is {row.Bytes} bytes, and the largest document that one field returns is {options.MaxValueBytes}. Read its small fields, filter it with pathEq, or ask for the dataset as a file.");
        }

        var spent = Interlocked.Add(ref _spent, row.Bytes);
        if (spent > options.MaxRequestBytes)
        {
            throw RawJsonErrors.Fail(
                RawJsonErrors.RequestLimitCode,
                $"This request passes the {options.MaxRequestBytes} bytes of whole documents that one request may carry: it already served {spent} bytes. Ask for fewer rows, or for the small fields of each.");
        }
    }
}

/// <summary>
/// The rows of a table that the Api serves as a view (ADR-0041): a location, a bot type, a hideout part, a
/// price and a handbook entry state their small facts as fields, and the whole document is one guarded field.
/// </summary>
public sealed class ModDatabaseObjectJson : ObjectTypeExtension<ModDatabaseObject>
{
    protected override void Configure(IObjectTypeDescriptor<ModDatabaseObject> d)
    {
        // The key of the row is read with every row, for the same reason: the resolver of rawJson needs it to
        // read the document of this row and no other. The other small fields are read when a client asks.
        d.Field(x => x.DatasetId).IsProjected(true);
        d.Field(x => x.Kind).IsProjected(true);
        d.Field(x => x.TemplateId).IsProjected(true);
        d.Field(x => x.Part).IsProjected(true);

        // The size of the document is a column, and it is read with every row for a reason: the resolver of
        // rawJson decides with it whether it may read the document at all.
        d.Field(x => x.Bytes).IsProjected(true)
            .Description("The size of the source in bytes, as the run wrote it. PostgreSQL renders the same document a few percent larger.");

        // The stored text is not a field: a client receives the document as JSON (TD-15) or not at all, and
        // the text of a large document is never a value to hand out unguarded.
        d.Field(x => x.Data).Ignore();

        d.Field("rawJson")
            .Type<AnyType>()
            .Cost(GraphQLSetup.RawJsonCost)
            .Description($"The whole source of the object, as JSON. It costs {GraphQLSetup.RawJsonCost} times a small field: prefer the small fields, or pathEq. A document larger than RawJson:MaxValueBytes answers RAW_JSON_TOO_LARGE.")
            .Resolve(ResolveAsync);
    }

    private static async Task<object?> ResolveAsync(IResolverContext context, CancellationToken cancellationToken)
    {
        var row = context.Parent<ModDatabaseObject>();
        var options = context.Service<IOptions<RawJsonOptions>>().Value;

        // One budget for the whole operation, made the first time a field asks for it. Before the read,
        // because the read is what costs.
        var budget = context.GetOrSetGlobalState(RawJsonBudget.StateKey, _ => new RawJsonBudget(options));
        budget.Charge(row);

        // A context of its own, because the fields of one page resolve at the same time and a DbContext is not
        // safe to use twice at once. The condition goes into the statement, so a document above the limit is
        // never assembled in memory even if the size of the row disagrees with the document.
        var factory = context.Service<IDbContextFactory<ArchiveDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var text = await db.ModDatabaseObjects
            .Where(x => x.DatasetId == row.DatasetId && x.Kind == row.Kind && x.TemplateId == row.TemplateId && x.Part == row.Part)
            .Select(x => x.Bytes <= options.MaxValueBytes ? x.Data : null)
            .FirstOrDefaultAsync(cancellationToken);

        return JsonText.Parse(text);
    }
}
