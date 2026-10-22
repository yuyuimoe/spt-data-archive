using System.Text.Json;

namespace SptArchive.Core.Canonical;

/// <summary>
/// How an element of an array is named in the address of a change (TD-72, ADR-0042 action item 7).
/// An element is named by what it is: the value of a string, or the first identity field of an object.
/// A position names a different object in another mod, so it is the last resort and it is never silent.
/// </summary>
public static class ArrayIdentity
{
    /// <summary>The identity fields of an object, in the order the repository prefers (ADR-0034).</summary>
    private static readonly string[] Fields =
        ["_id", "Id", "_name", "Name", "QuestName", "template.Id", "locationId", "Role", "id", "tpl"];

    /// <summary>
    /// The identity of each element, in array order, or null when the array cannot be keyed: an element has
    /// no identity (none of the fields, an empty one, or a value that is not a string) or two elements share one.
    /// An empty array has an empty list of keys. <paramref name="reason"/> says why a key list is null.
    /// </summary>
    /// <param name="array">The array to read.</param>
    /// <param name="map">Writes a string the way the canonical form does, so an id is read as its address. Null keeps the text.</param>
    /// <param name="reason">What stopped the keying, as a short phrase that names the shape of the element.</param>
    public static List<string>? Keys(JsonElement array, Func<string, string>? map, out string? reason)
    {
        reason = null;
        var keys = new List<string>(array.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in array.EnumerateArray())
        {
            var key = Identity(element, map);

            if (key is null)
            {
                reason = "an element has no identity (" + Shape(element) + ")";
                return null;
            }

            if (!seen.Add(key))
            {
                reason = "two elements share an identity";
                return null;
            }

            keys.Add(key);
        }

        return keys;
    }

    /// <summary>The identity of one element, or null when it has none.</summary>
    public static string? Identity(JsonElement element, Func<string, string>? map)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return NonEmpty(element.GetString(), map);

            case JsonValueKind.Object:
                foreach (var field in Fields)
                {
                    if (Read(element, field) is { } text && NonEmpty(text, map) is { } key)
                    {
                        return key;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>"object {Source,Destination}" or "number": enough to find the kind in a log.</summary>
    public static string Shape(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? "object {" + string.Join(",", element.EnumerateObject().Select(p => p.Name).Take(6)) + "}"
            : element.ValueKind.ToString().ToLowerInvariant();

    private static string? NonEmpty(string? text, Func<string, string>? map) =>
        string.IsNullOrEmpty(text) ? null : map is null ? text : map(text);

    private static string? Read(JsonElement element, string field)
    {
        var current = element;

        foreach (var step in field.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(step, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
