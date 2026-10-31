namespace Bloom.Shared.Messaging;

/// <summary>One message type a consumer queue is bound for; the routing key is the message type.</summary>
public sealed record ConsumerBinding(string Kind, string MessageType, string? Producer = null)
{
    public static ConsumerBinding Event(string producer, string messageType) =>
        new(MessageKinds.Event, messageType, producer);

    /// <summary>Binds to this service's own <c>.commands</c> exchange.</summary>
    public static ConsumerBinding Command(string messageType) => new(MessageKinds.Command, messageType);

    internal string Exchange(string serviceName) => Kind == MessageKinds.Event
        ? MessageNames.EventExchange(Producer!)
        : MessageNames.CommandExchange(serviceName);
}
