using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bloom.Shared.Messaging;

internal static class BloomJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };
}
