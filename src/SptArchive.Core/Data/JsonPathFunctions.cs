using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace SptArchive.Core.Data;

/// <summary>
/// Reads the value at a path inside a jsonb document (ADR-0040). The methods run in PostgreSQL and never in C#:
/// EF Core translates each to <c>jsonb_extract_path(document, segment1, ...)</c>, which PostgreSQL documents as
/// equivalent to the operator <c>#&gt;</c>. A segment is an object key or an array index, written as text. The
/// result is jsonb, or SQL NULL when the document holds nothing at the path.
/// </summary>
/// <remarks>
/// The path is one argument for each segment and not an array, because <c>jsonb_extract_path</c> takes an
/// array only after the keyword <c>VARIADIC</c>, which EF Core cannot write. Every segment is a query parameter,
/// so a path never becomes part of the statement text. One overload exists for each path length up to
/// <see cref="MaxSegments"/>.
/// </remarks>
public static class JsonPathFunctions
{
    /// <summary>The longest path, and so the highest number of overloads of <see cref="Extract(string?, string)"/>.</summary>
    public const int MaxSegments = 10;

    private const string TranslatedOnly = "This method is translated to SQL and cannot run in C#.";

    public static string? Extract(string? document, string segment1) => throw new NotSupportedException(TranslatedOnly);
    public static string? Extract(string? document, string segment1, string segment2) => throw new NotSupportedException(TranslatedOnly);
    public static string? Extract(string? document, string segment1, string segment2, string segment3) => throw new NotSupportedException(TranslatedOnly);
    public static string? Extract(string? document, string segment1, string segment2, string segment3, string segment4) => throw new NotSupportedException(TranslatedOnly);
    public static string? Extract(string? document, string segment1, string segment2, string segment3, string segment4, string segment5) => throw new NotSupportedException(TranslatedOnly);
    public static string? Extract(string? document, string segment1, string segment2, string segment3, string segment4, string segment5, string segment6) => throw new NotSupportedException(TranslatedOnly);
    public static string? Extract(string? document, string segment1, string segment2, string segment3, string segment4, string segment5, string segment6, string segment7) => throw new NotSupportedException(TranslatedOnly);
    public static string? Extract(string? document, string segment1, string segment2, string segment3, string segment4, string segment5, string segment6, string segment7, string segment8) => throw new NotSupportedException(TranslatedOnly);
    public static string? Extract(string? document, string segment1, string segment2, string segment3, string segment4, string segment5, string segment6, string segment7, string segment8, string segment9) => throw new NotSupportedException(TranslatedOnly);
    public static string? Extract(string? document, string segment1, string segment2, string segment3, string segment4, string segment5, string segment6, string segment7, string segment8, string segment9, string segment10) => throw new NotSupportedException(TranslatedOnly);

    /// <summary>Maps every overload of <see cref="Extract(string?, string)"/> to the PostgreSQL function.</summary>
    internal static void Register(ModelBuilder modelBuilder)
    {
        foreach (var method in typeof(JsonPathFunctions).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == nameof(Extract)))
        {
            modelBuilder.HasDbFunction(method).HasTranslation(arguments => new SqlFunctionExpression(
                "jsonb_extract_path",
                arguments,
                nullable: true,
                // The result is NULL for a path that the document lacks, even when every argument has a value.
                argumentsPropagateNullability: [.. arguments.Select(_ => false)],
                typeof(string),
                arguments[0].TypeMapping));
        }
    }
}
