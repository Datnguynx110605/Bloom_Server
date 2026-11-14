using System.Buffers.Binary;
using System.Buffers.Text;
using Bloom.Notification.Context;
using Bloom.Notification.Data;
using Bloom.Notification.Errors;
using Bloom.Notification.Protos;
using Bloom.Notification.Sockets;
using Bloom.Shared.Persistence;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Notification.Services;

/// <summary>09-notification §5.1 customer API (spec §6). Every call is the caller's own account only (N8: role check first, then N9 validation).</summary>
public sealed class CustomerService(NotificationDbContext db, Tickets tickets) : Notifications.NotificationsBase
{
    private const int PageSize = 20;
    private const int TokenBytes = 24; // int64 µs since the epoch ‖ 16 id bytes

    public override async Task<ListNotificationsResponse> ListNotifications(ListNotificationsRequest request, ServerCallContext context)
    {
        var accountId = Caller.From(context).RequireCustomer().UserId;
        (DateTimeOffset CreatedAt, Guid Id)? after = request.PageToken == "" ? null : ParseToken(request.PageToken);
        var ct = context.CancellationToken;

        // Keyset on (created_at, id): uuid compares bytewise in PostgreSQL, so raw SQL rather than Guid.CompareTo.
        var rows = await (after is { } a
            ? db.Notifications.FromSql($"SELECT * FROM notifications WHERE account_id = {accountId} AND (created_at, id) < ({a.CreatedAt}, {a.Id}) ORDER BY created_at DESC, id DESC LIMIT {PageSize + 1}")
            : db.Notifications.FromSql($"SELECT * FROM notifications WHERE account_id = {accountId} ORDER BY created_at DESC, id DESC LIMIT {PageSize + 1}"))
            .AsNoTracking().ToListAsync(ct);

        var response = new ListNotificationsResponse();
        response.Notifications.AddRange(rows.Take(PageSize).Select(n => new NotificationItem
        {
            Id = n.Id.ToString("D"), Type = n.Type, Title = n.Title, Body = n.Body, Link = n.Link, Read = n.ReadAt is not null, CreatedAt = Timestamp.FromDateTimeOffset(n.CreatedAt),
        }));
        if (rows.Count > PageSize) response.NextPageToken = Token(rows[PageSize - 1]);
        return response;
    }

    public override async Task<GetUnreadCountResponse> GetUnreadCount(GetUnreadCountRequest request, ServerCallContext context)
    {
        var accountId = Caller.From(context).RequireCustomer().UserId;
        return new GetUnreadCountResponse { Count = await db.Notifications.CountAsync(n => n.AccountId == accountId && n.ReadAt == null, context.CancellationToken) };
    }

    public override async Task<Empty> MarkRead(MarkReadRequest request, ServerCallContext context)
    {
        var accountId = Caller.From(context).RequireCustomer().UserId;
        if (request.Ids.Count is < 1 or > 100) throw NotificationErrors.Validation("ids", "ids must hold 1 to 100 UUIDs.");
        var ids = request.Ids.Select(id => Guid.TryParseExact(id, "D", out var g) ? g : throw NotificationErrors.Validation("ids", "Every id must be a UUID.")).ToList();

        var now = await db.Database.NowAsync(context.CancellationToken);
        await db.Notifications.Where(n => n.AccountId == accountId && ids.Contains(n.Id) && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), context.CancellationToken);
        return new Empty();
    }

    public override async Task<Empty> MarkAllRead(MarkAllReadRequest request, ServerCallContext context)
    {
        var accountId = Caller.From(context).RequireCustomer().UserId;
        var now = await db.Database.NowAsync(context.CancellationToken);
        await db.Notifications.Where(n => n.AccountId == accountId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), context.CancellationToken);
        return new Empty();
    }

    public override async Task<GetWsTicketResponse> GetWsTicket(GetWsTicketRequest request, ServerCallContext context) =>
        new() { Ticket = await tickets.IssueAsync(Caller.From(context).RequireCustomer().UserId) };

    private static string Token(NotificationRow last)
    {
        var bytes = new byte[TokenBytes];
        BinaryPrimitives.WriteInt64BigEndian(bytes, (last.CreatedAt - DateTimeOffset.UnixEpoch).Ticks / 10);
        last.Id.TryWriteBytes(bytes.AsSpan(8), bigEndian: true, out _);
        return Base64Url.EncodeToString(bytes);
    }

    private static (DateTimeOffset CreatedAt, Guid Id) ParseToken(string token)
    {
        try
        {
            var bytes = Base64Url.DecodeFromChars(token);
            if (bytes.Length == TokenBytes)
                return (DateTimeOffset.UnixEpoch.AddTicks(BinaryPrimitives.ReadInt64BigEndian(bytes) * 10), new Guid(bytes.AsSpan(8), bigEndian: true));
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or OverflowException) { }
        throw NotificationErrors.Validation("page_token", "page_token is not a token this API returned.");
    }
}
