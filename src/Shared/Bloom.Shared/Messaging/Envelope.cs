using System.Text.Json;

namespace Bloom.Shared.Messaging;

public sealed record Envelope(
    Guid MessageId,
    string MessageType,
    string Kind,
    string Producer,
    DateTime OccurredAt,
    Guid CorrelationId,
    JsonElement Payload,
    Guid? CausationId = null)
{
    public byte[] ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, BloomJson.Options);

    /// <exception cref="JsonException">Not a valid envelope (malformed JSON or a required field missing).</exception>
    public static Envelope Parse(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<Envelope>(utf8, BloomJson.Options) ?? throw new JsonException("Envelope is null.");

    public T GetPayload<T>()
    {
        try
        {
            return Payload.Deserialize<T>(BloomJson.Options) ?? throw new JsonException("Payload is null.");
        }
        catch (JsonException e)
        {
            throw new PermanentFailureException($"Payload of {MessageType} is not a valid {typeof(T).Name}.", e);
        }
    }
}
