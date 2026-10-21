using System.Text.Json;
using HotChocolate;
using HotChocolate.Language;
using SptArchive.Core.Data;

namespace SptArchive.Api.GraphQL;

/// <summary>
/// The error codes of the JSON filters (ADR-0040). A client branches on the code and not on the text, as it does
/// for the release errors of <see cref="Releases"/>.
/// </summary>
public static class JsonFilterErrors
{
    /// <summary>The path of a path filter is empty, too long, too deep or holds an empty segment.</summary>
    public const string PathMalformedCode = "JSON_PATH_MALFORMED";

    /// <summary>The value of a JSON filter is missing or is not JSON.</summary>
    public const string ValueMalformedCode = "JSON_VALUE_MALFORMED";

    /// <summary>The value is JSON, and the database still refuses it: a NUL character or a number out of range.</summary>
    public const string ValueRejectedCode = "JSON_VALUE_REJECTED";

    public static GraphQLException Fail(string code, string message) =>
        new(ErrorBuilder.New().SetMessage(message).SetCode(code).Build());
}

/// <summary>Reads the arguments of a JSON filter into the paths and the JSON text that the query parameters carry.</summary>
internal static class JsonFilterArguments
{
    /// <summary>The longest segment of a path, in characters.</summary>
    public const int MaxSegmentLength = 256;

    // The default encoder writes a quote as \u0022, which a reader of the message cannot use as an example.
    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The deepest JSON value that a path filter compares with. PostgreSQL reads any depth; the Api reads a bounded one.</summary>
    public const int MaxValueDepth = 32;

    /// <summary>Reads the <c>path</c> and the <c>value</c> of a <c>pathEq</c> or <c>pathNeq</c> operation.</summary>
    public static (string[] Path, string Json) ReadCondition(IValueNode condition)
    {
        if (condition is not ObjectValueNode fields)
        {
            throw JsonFilterErrors.Fail(JsonFilterErrors.PathMalformedCode, "A path filter takes an object with a 'path' and a 'value'.");
        }

        var path = fields.Fields.FirstOrDefault(f => f.Name.Value == "path")?.Value;
        var value = fields.Fields.FirstOrDefault(f => f.Name.Value == "value");

        if (value is null)
        {
            throw JsonFilterErrors.Fail(
                JsonFilterErrors.ValueMalformedCode,
                "A path filter needs a 'value'. Write null to compare with the JSON value null.");
        }

        return (ReadPath(path), ToJson(value.Value));
    }

    /// <summary>
    /// A path is one to <see cref="JsonPathFunctions.MaxSegments"/> segments. Each segment is an object key or an
    /// array index, from 1 to <see cref="MaxSegmentLength"/> characters, with no NUL character.
    /// </summary>
    public static string[] ReadPath(IValueNode? node)
    {
        var segments = node switch
        {
            ListValueNode list => list.Items.Select(ReadSegment).ToArray(),
            StringValueNode single => [single.Value],
            _ => throw JsonFilterErrors.Fail(JsonFilterErrors.PathMalformedCode, "The 'path' of a path filter is a list of strings."),
        };

        if (segments.Length is 0 or > JsonPathFunctions.MaxSegments)
        {
            throw JsonFilterErrors.Fail(
                JsonFilterErrors.PathMalformedCode,
                $"The path has {segments.Length} segments. A path has 1 to {JsonPathFunctions.MaxSegments}.");
        }

        return segments;
    }

    private static string ReadSegment(IValueNode node)
    {
        if (node is not StringValueNode { Value: var segment })
        {
            throw JsonFilterErrors.Fail(JsonFilterErrors.PathMalformedCode, "Every segment of a path is a string. Write an array index as text, for example \"1\".");
        }

        if (segment.Length is 0 or > MaxSegmentLength || segment.Contains('\0'))
        {
            throw JsonFilterErrors.Fail(
                JsonFilterErrors.PathMalformedCode,
                $"A segment of a path has 1 to {MaxSegmentLength} characters and no NUL character.");
        }

        return segment;
    }

    /// <summary>
    /// Writes a GraphQL literal as JSON. A number keeps its text, so a large integer is not rounded on the way.
    /// A bare word (an enum literal) is not JSON and is refused.
    /// </summary>
    public static string ToJson(IValueNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, node, depth: 0);
        }

        return System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static void Write(Utf8JsonWriter writer, IValueNode node, int depth)
    {
        if (depth > MaxValueDepth)
        {
            throw JsonFilterErrors.Fail(JsonFilterErrors.ValueMalformedCode, $"The value is nested deeper than {MaxValueDepth} levels.");
        }

        switch (node)
        {
            case NullValueNode:
                writer.WriteNullValue();
                break;
            case BooleanValueNode boolean:
                writer.WriteBooleanValue(boolean.Value);
                break;
            case StringValueNode text:
                writer.WriteStringValue(text.Value);
                break;
            case IntValueNode or FloatValueNode:
                writer.WriteRawValue(node.ToString());
                break;
            case ListValueNode list:
                writer.WriteStartArray();
                foreach (var item in list.Items)
                {
                    Write(writer, item, depth + 1);
                }

                writer.WriteEndArray();
                break;
            case ObjectValueNode obj:
                writer.WriteStartObject();
                foreach (var field in obj.Fields)
                {
                    writer.WritePropertyName(field.Name.Value);
                    Write(writer, field.Value, depth + 1);
                }

                writer.WriteEndObject();
                break;
            default:
                throw JsonFilterErrors.Fail(
                    JsonFilterErrors.ValueMalformedCode,
                    "The value is a bare word and not JSON. Write a string in quotes, a number, true, false, null, a list or an object.");
        }
    }

    /// <summary>
    /// Checks the JSON text that <c>eq</c> and <c>neq</c> compare a whole document with. The text goes to the
    /// jsonb parameter as it is, so text that is not JSON would fail in the database as an internal error (TD-55).
    /// </summary>
    public static string ValidateText(string? text)
    {
        try
        {
            using var document = JsonDocument.Parse(text ?? string.Empty);
            return text!;
        }
        catch (JsonException)
        {
            var shown = text is { Length: > 50 } ? text[..50] + "..." : text;
            var example = JsonSerializer.Serialize(JsonSerializer.Serialize(shown, Readable), Readable);
            throw JsonFilterErrors.Fail(
                JsonFilterErrors.ValueMalformedCode,
                $"The value {JsonSerializer.Serialize(shown, Readable)} is not JSON text. The operations eq and neq compare a whole document and take JSON text, so a string is written with its quotes, for example {example}. To compare the value at a path, use pathEq.");
        }
    }
}
