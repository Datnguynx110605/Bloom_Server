using Bloom.Notification.Observability;

namespace Bloom.Notification.Sockets;

/// <summary>
/// Runs before the hub on its path and every path under it and answers without a body (owner item 1): not a WebSocket upgrade 400 (the ticket stays),
/// Origin not allowed 403 (the ticket stays), unknown or used ticket 401. The ticket is spent last, so only an allowed upgrade can burn it.
/// </summary>
public sealed class HubGate(RequestDelegate next, NotificationOptions options, Tickets tickets, NotificationMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(NotificationHub.Path, StringComparison.OrdinalIgnoreCase)) // the hub path and everything under it (negotiate, trailing slash)
        {
            await next(context);
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!options.AllowedOrigins.Contains(context.Request.Headers.Origin.ToString(), StringComparer.OrdinalIgnoreCase)) // a missing Origin is "" and never allowed
        {
            metrics.Rejected("origin");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var ticket = context.Request.Query["ticket"].ToString();
        var accountId = ticket.Length == 0 ? null : await tickets.RedeemAsync(ticket);
        if (accountId is null)
        {
            metrics.Rejected("ticket");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        context.Items[AccountItem] = accountId.Value;
        await next(context);
    }

    public const string AccountItem = "accountId";
}
