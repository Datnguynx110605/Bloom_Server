using System.Text;
using System.Text.Json;
using Bloom.Shared.Messaging;

namespace Bloom.Shared.Tests;

public class MessageContractTests
{
    private sealed record TestPayload(string Sku, int Quantity);

    private static Envelope Sample(Guid? causationId = null) => new(
        Guid.NewGuid(), "order.paid.v1", MessageKinds.Event, "order", DateTime.UtcNow, Guid.NewGuid(),
        JsonSerializer.SerializeToElement(new { sku = "a", quantity = 2 }), causationId);

    [Fact]
    public void Names_follow_5_1()
    {
        Assert.Equal("order.events", MessageNames.EventExchange("order"));
        Assert.Equal("inventory.commands", MessageNames.CommandExchange("inventory"));
        Assert.Equal("inventory.order-paid", MessageNames.ConsumerQueue("inventory", "order-paid"));
        Assert.Equal("inventory.order-paid.retry.30s", MessageNames.RetryQueue("inventory.order-paid", "30s"));
        Assert.Equal("inventory.order-paid.dlq", MessageNames.DeadLetterQueue("inventory.order-paid"));
        Assert.Equal(new[] { ("1s", 1000), ("5s", 5000), ("30s", 30000), ("2m", 120000) }, MessageNames.RetryDelays);
    }

    [Theory]
    [InlineData("order.paid.v1", true)]
    [InlineData("inventory.release-reservation.v1", true)]
    [InlineData("identity.staff-action-recorded.v12", true)]
    [InlineData("Order.paid.v1", false)]
    [InlineData("order.paid", false)]
    [InlineData("order.v1", false)]
    [InlineData("order.paid.v0", false)]
    [InlineData("order..paid.v1", false)]
    [InlineData("order.paid.v1\n", false)]
    public void Message_type_format(string type, bool valid) =>
        Assert.Equal(valid, MessageNames.IsValidMessageType(type));

    [Fact]
    public void Envelope_serializes_with_5_2_field_names()
    {
        using var doc = JsonDocument.Parse(Sample().ToUtf8Bytes());
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(
            new[] { "messageId", "messageType", "kind", "producer", "occurredAt", "correlationId", "payload" }, names);
        Assert.EndsWith("Z", doc.RootElement.GetProperty("occurredAt").GetString());

        var causation = Guid.NewGuid();
        using var withCausation = JsonDocument.Parse(Sample(causation).ToUtf8Bytes());
        Assert.Equal(causation, withCausation.RootElement.GetProperty("causationId").GetGuid());
    }

    [Fact]
    public void Envelope_round_trips()
    {
        var e = Sample(Guid.NewGuid());
        var parsed = Envelope.Parse(e.ToUtf8Bytes());
        Assert.Equal(e.MessageId, parsed.MessageId);
        Assert.Equal(e.CausationId, parsed.CausationId);
        Assert.Equal(e.OccurredAt, parsed.OccurredAt);
        Assert.Equal(new TestPayload("a", 2), parsed.GetPayload<TestPayload>());
    }

    [Fact]
    public void Envelope_ignores_unknown_fields()
    {
        var json = $$$"""
            {"messageId":"{{{Guid.NewGuid()}}}","messageType":"order.paid.v1","kind":"event","producer":"order",
             "occurredAt":"2026-10-31T10:00:00Z","correlationId":"{{{Guid.NewGuid()}}}","payload":{},"foo":1}
            """;
        Assert.Equal("order.paid.v1", Envelope.Parse(Encoding.UTF8.GetBytes(json)).MessageType);
    }

    [Fact]
    public void Envelope_missing_required_field_throws()
    {
        var json = $$$"""
            {"messageType":"order.paid.v1","kind":"event","producer":"order",
             "occurredAt":"2026-10-31T10:00:00Z","correlationId":"{{{Guid.NewGuid()}}}","payload":{}}
            """;
        Assert.ThrowsAny<JsonException>(() => Envelope.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void GetPayload_missing_required_field_is_permanent()
    {
        var e = Sample() with { Payload = JsonDocument.Parse("""{"sku":"a"}""").RootElement };
        Assert.Throws<PermanentFailureException>(() => e.GetPayload<TestPayload>());
    }
}
