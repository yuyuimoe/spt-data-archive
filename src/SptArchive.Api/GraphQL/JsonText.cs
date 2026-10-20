using System.Text.Json;

namespace SptArchive.Api.GraphQL;

/// <summary>Reads the text of a jsonb column for the <c>Any</c> scalar of HotChocolate (TD-15).</summary>
internal static class JsonText
{
    /// <summary>Parses the text of a jsonb column. Null stays null.</summary>
    public static JsonElement? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
