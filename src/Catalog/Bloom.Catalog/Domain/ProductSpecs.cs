using System.Text.Json;
using Bloom.Catalog.V1;

namespace Bloom.Catalog.Domain;

/// <summary>The <c>specs</c> jsonb shape of 08-catalog §4.1: <c>[{group, items: [{label, value}]}]</c>.</summary>
internal sealed record SpecItemJson(string Label, string Value);

internal sealed record SpecGroupJson(string Group, List<SpecItemJson> Items);

internal static class ProductSpecs
{
    public static List<SpecGroupJson> Parse(string json) =>
        JsonSerializer.Deserialize<List<SpecGroupJson>>(json, JsonSerializerOptions.Web) ?? [];

    public static IEnumerable<SpecGroup> ToProto(string json) => Parse(json).Select(g =>
        new SpecGroup { Group = g.Group, Items = { g.Items.Select(i => new SpecItem { Label = i.Label, Value = i.Value }) } });
}
