using Microsoft.AspNetCore.SignalR;

namespace Bloom.Notification.Sockets;

/// <summary>What a client receives as <see cref="NotificationHub.ClientMethod"/> (09-notification §9).</summary>
public sealed record PushMessage(Guid Id, string Type, string Title, string Link, DateTimeOffset CreatedAt);

/// <summary>Push-only hub: clients call nothing. <see cref="HubGate"/> has already spent the ticket and stored the account (09-notification §4.2, §6.3).</summary>
public sealed class NotificationHub(ConnectionRegistry registry) : Hub
{
    public const string Path = "/api/notifications/hub";
    public const string ClientMethod = "notification";

    public static string Group(Guid accountId) => $"user:{accountId:D}";

    public override async Task OnConnectedAsync()
    {
        if (Context.GetHttpContext()?.Items[HubGate.AccountItem] is not Guid accountId)
        {
            Context.Abort(); // fail closed: only HubGate sets the account
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(accountId));
        registry.Add(accountId, Context);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Remove(Context);
        return Task.CompletedTask;
    }
}
