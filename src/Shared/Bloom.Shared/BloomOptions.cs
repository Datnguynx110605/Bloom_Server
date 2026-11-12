namespace Bloom.Shared;

public sealed class BloomOptions
{
    /// <summary>Lowercase service name, for example <c>order</c>. Must match <c>^[a-z][a-z0-9-]*$</c>.</summary>
    public string ServiceName { get; set; } = "";

    public string RabbitMqUri { get; set; } = "amqp://guest:guest@localhost:5672/";
}
