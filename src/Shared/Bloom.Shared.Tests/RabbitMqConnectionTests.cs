using System.Reflection;
using Bloom.Shared.Messaging;
using RabbitMQ.Client;

namespace Bloom.Shared.Tests;

public class RabbitMqConnectionTests(Containers containers)
{
    /// <summary>A closed connection whose disposal throws, as a connection torn down mid-handshake can.</summary>
    public class Broken : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_IsOpen" => false,
            "DisposeAsync" => throw new InvalidOperationException("dispose failed"),
            _ => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null,
        };
    }

    [Fact]
    public async Task Stale_connection_that_fails_to_dispose_is_still_replaced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new RabbitMqConnection(new BloomOptions { ServiceName = "svc", RabbitMqUri = containers.RabbitMqUri });
        typeof(RabbitMqConnection).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(connection, DispatchProxy.Create<IConnection, Broken>());

        var fresh = await connection.GetAsync(ct);

        Assert.True(fresh.IsOpen);
    }
}
