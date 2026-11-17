using System.Text.Json;

namespace Chronicle.Plugin.Wikidata;

/// <summary>Tolerant readers for Wikidata JSON: a missing or wrongly-typed member is "absent", never an exception (E3).</summary>
internal static class WdJson
{
    public static JsonElement? Obj(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p
        && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    public static string? Str(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty(name, out var v)) return null;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>The elements of <c>parent[name]</c> that are objects (empty when absent or not an array).</summary>
    public static IReadOnlyList<JsonElement> List(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty(name, out var v)
            || v.ValueKind != JsonValueKind.Array)
            return [];
        return v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
    }

    /// <summary>The members of an object, in order (empty when absent or not an object).</summary>
    public static IEnumerable<JsonProperty> Members(JsonElement? parent, string name) =>
        Obj(parent, name) is { } o ? o.EnumerateObject() : [];
}
