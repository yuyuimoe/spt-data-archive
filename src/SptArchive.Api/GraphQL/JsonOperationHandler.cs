using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using HotChocolate.Configuration;
using HotChocolate.Data.Filters;
using HotChocolate.Data.Filters.Expressions;
using HotChocolate.Language;
using SptArchive.Core.Data;

namespace SptArchive.Api.GraphQL;

/// <summary>The identifiers of the operations of <see cref="JsonOperationFilterInputType"/>. They are above the ones that HotChocolate defines.</summary>
public static class JsonFilterOperations
{
    public const int DocumentEquals = 1_001;
    public const int DocumentNotEquals = 1_002;
    public const int PathEquals = 1_003;
    public const int PathNotEquals = 1_004;
}

/// <summary>
/// Translates the operations of a jsonb filter (ADR-0040). A comparison is a jsonb comparison, so PostgreSQL
/// compares the content of two documents and not their text. Every value that a client sends reaches the query as
/// a parameter: the query cache of EF Core holds one entry for each path length and not one for each value.
/// </summary>
public sealed class JsonOperationHandler : FilterOperationHandler<QueryableFilterContext, Expression>
{
    public override bool CanHandle(
        ITypeCompletionContext context, IFilterInputTypeConfiguration typeConfiguration, IFilterFieldConfiguration fieldConfiguration) =>
        fieldConfiguration is FilterOperationFieldConfiguration { Id: >= JsonFilterOperations.DocumentEquals and <= JsonFilterOperations.PathNotEquals };

    public override bool TryHandleOperation(
        QueryableFilterContext context,
        IFilterOperationField field,
        ObjectFieldNode node,
        [NotNullWhen(true)] out Expression? result)
    {
        var document = context.GetInstance();

        result = field.Id switch
        {
            JsonFilterOperations.DocumentEquals => Expression.Equal(document, Parameter(ReadText(node))),
            JsonFilterOperations.DocumentNotEquals => Expression.NotEqual(document, Parameter(ReadText(node))),
            _ => ComparePath(document, node.Value, negate: field.Id == JsonFilterOperations.PathNotEquals),
        };

        return true;
    }

    private static string ReadText(ObjectFieldNode node) => node.Value switch
    {
        StringValueNode text => JsonFilterArguments.ValidateText(text.Value),
        _ => throw JsonFilterErrors.Fail(JsonFilterErrors.ValueMalformedCode, "The value of eq and neq is JSON text, written as a string."),
    };

    /// <summary>
    /// <c>jsonb_extract_path(document, segment, ...) = value</c>. A path that the document lacks gives NULL, so
    /// <c>pathEq</c> never matches it and <c>pathNeq</c> always does, as the C# operator <c>!=</c> says.
    /// </summary>
    private static Expression ComparePath(Expression document, IValueNode condition, bool negate)
    {
        var (path, json) = JsonFilterArguments.ReadCondition(condition);

        var extract = Expression.Call(
            typeof(JsonPathFunctions).GetMethod(nameof(JsonPathFunctions.Extract), [.. Enumerable.Repeat(typeof(string), path.Length + 1)])!,
            [document, .. path.Select(Parameter)]);

        return negate ? Expression.NotEqual(extract, Parameter(json)) : Expression.Equal(extract, Parameter(json));
    }

    // A member of a captured object is what EF Core turns into a query parameter. A constant would become a literal.
    private static MemberExpression Parameter(string value) =>
        Expression.Property(Expression.Constant(new Captured(value)), nameof(Captured.Value));

    private sealed record Captured(string Value);
}
