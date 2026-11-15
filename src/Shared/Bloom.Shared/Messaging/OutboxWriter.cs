using System.Diagnostics;
using System.Text.Json;
using Bloom.Shared.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Shared.Messaging;

/// <summary>
/// Adds an <c>outbox_messages</c> row to the caller's <see cref="DbContext"/> (LLD 00-reliability §7.1).
/// Never calls <c>SaveChanges</c>: the row commits or rolls back with the business change (RL-01).
/// </summary>
public sealed class OutboxWriter(DbContext db, BloomOptions options, IHttpContextAccessor http)
{
    private const string RequestIdHeader = "x-bloom-request-id";

    public Guid PublishEvent<TPayload>(string messageType, TPayload payload) =>
        Write(MessageKinds.Event, MessageNames.EventExchange(options.ServiceName), messageType, payload);

    public Guid SendCommand<TPayload>(string targetService, string messageType, TPayload payload) =>
        Write(MessageKinds.Command, MessageNames.CommandExchange(targetService), messageType, payload);

    private Guid Write<TPayload>(string kind, string exchange, string messageType, TPayload payload)
    {
        if (!MessageNames.IsValidMessageType(messageType))
            throw new ArgumentException($"'{messageType}' is not a valid message type.", nameof(messageType));

        var handling = MessageScope.Current.Value;
        var correlationId = handling?.CorrelationId
            ?? (Guid.TryParseExact(http.HttpContext?.Request.Headers[RequestIdHeader].ToString(), "D", out var requestId)
                ? requestId
                : Guid.CreateVersion7());
        var causationId = handling?.MessageId;
        var id = Guid.CreateVersion7();

        var envelope = new Envelope(id, messageType, kind, options.ServiceName, default /* set by the relay from created_at (00-reliability §5.2) */, correlationId,
            JsonSerializer.SerializeToElement(payload, BloomJson.Options), causationId);

        var headers = new Dictionary<string, string>();
        if (Activity.Current?.Id is { } traceparent) headers["traceparent"] = traceparent;
        headers["correlation_id"] = correlationId.ToString();
        if (causationId is { } cause) headers["causation_id"] = cause.ToString();

        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = id,
            MessageType = messageType,
            Kind = kind,
            Exchange = exchange,
            RoutingKey = messageType,
            Payload = JsonSerializer.Serialize(envelope, BloomJson.Options),
            Headers = JsonSerializer.Serialize(headers),
            Status = "pending",
        });
        return id;
    }
}
