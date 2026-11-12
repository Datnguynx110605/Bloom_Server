using Bloom.Notification.Observability;
using Microsoft.AspNetCore.SignalR;

namespace Bloom.Notification.Sockets;

/// <summary>This pod's open sockets by account, each with the timer that closes it 15 minutes after it opened (09-notification §6.3; spec §7 Sockets).</summary>
public sealed class ConnectionRegistry(TimeProvider clock, NotificationMetrics metrics)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private const string AccountKey = "registry:account";

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Dictionary<string, (HubCallerContext Context, ITimer Timer)>> _byAccount = [];

    /// <summary>Open sockets on this pod; tests wait on it, because a client is connected before the hub has finished <c>OnConnectedAsync</c>.</summary>
    internal int Count { get { lock (_lock) return _byAccount.Values.Sum(s => s.Count); } }

    public void Add(Guid accountId, HubCallerContext ctx)
    {
        var timer = clock.CreateTimer(_ => ctx.Abort(), null, Lifetime, Timeout.InfiniteTimeSpan);
        ctx.Items[AccountKey] = accountId;
        metrics.ConnectionOpened();
        lock (_lock)
        {
            if (!_byAccount.TryGetValue(accountId, out var sockets)) _byAccount[accountId] = sockets = [];
            sockets[ctx.ConnectionId] = (ctx, timer);
        }
    }

    /// <summary>Forgets the connection and stops its timer; does nothing for one that never made it into the registry.</summary>
    public void Remove(HubCallerContext ctx)
    {
        if (ctx.Items[AccountKey] is not Guid accountId) return;
        ITimer timer;
        lock (_lock)
        {
            if (!_byAccount.TryGetValue(accountId, out var sockets) || !sockets.Remove(ctx.ConnectionId, out var entry)) return;
            if (sockets.Count == 0) _byAccount.Remove(accountId);
            timer = entry.Timer;
        }
        timer.Dispose();
        metrics.ConnectionClosed();
    }

    /// <summary>Aborts every socket of the account on this pod; <see cref="Remove"/> follows from the hub's disconnect. Returns how many.</summary>
    public int AbortAll(Guid accountId)
    {
        HubCallerContext[] contexts;
        lock (_lock) contexts = _byAccount.TryGetValue(accountId, out var sockets) ? [.. sockets.Values.Select(s => s.Context)] : [];
        foreach (var ctx in contexts) ctx.Abort();
        return contexts.Length;
    }
}
