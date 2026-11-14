using System.Text.Json;
using Bloom.Fulfillment.Data;
using Bloom.Order.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Consumers;

/// <summary>
/// <c>order.ready-to-fulfill.v1</c> (05-fulfillment §6.1, §4.1, §4.2): creates the <c>pending</c> pack task, its lines and the money accounts.
/// A task already on <c>ref_id = order_id</c> (a duplicate delivery, or the cancellation marker of F-02) wins: nothing else is written.
/// Runs in <c>ConsumerHost</c>'s transaction (plan R8); the lines are saved with it.
/// </summary>
public sealed class ReadyToFulfillConsumer(FulfillmentDbContext db) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("order", OrderMessages.ReadyToFulfill)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var ready = envelope.GetPayload<OrderReadyToFulfillV1>();
        Validate(ready);

        var taskId = Guid.CreateVersion7();
        var shipTo = JsonSerializer.Serialize(ready.ShipTo, JsonSerializerOptions.Web);
        if (await db.Database.ExecuteSqlAsync($"""
            INSERT INTO pack_tasks (id, ref_id, order_id, kind, status, cancelled_before_arrival, ship_to, shipping_option, version, created_at, updated_at)
            VALUES ({taskId}, {ready.OrderId}, {ready.OrderId}, {TaskKinds.Order}, {TaskStatuses.Pending}, false, {shipTo}::jsonb, {ready.ShippingOption}, 0, now(), now())
            ON CONFLICT (ref_id) DO NOTHING
            """, ct) == 0)
            return;

        db.OrderAccounts.Add(new OrderAccount { OrderId = ready.OrderId, AccountId = ready.AccountId, ShippingCents = ready.ShippingCents, TotalCents = ready.TotalCents });
        foreach (var l in ready.Lines)
        {
            db.PackTaskLines.Add(new PackTaskLine
            {
                TaskId = taskId, LineNo = l.LineNo, Kind = l.Kind, SkuId = l.SkuId, ProductId = l.ProductId, Serialized = l.Serialized,
                Quantity = l.Quantity, ParentLineNo = l.ParentLineNo, CoverageMonths = l.CoverageMonths,
            });
            db.OrderLineAccounts.Add(new OrderLineAccount
            {
                OrderId = ready.OrderId, LineNo = l.LineNo, Kind = l.Kind, ParentLineNo = l.ParentLineNo, Quantity = l.Quantity, PaidCents = l.PaidCents, TaxCents = l.TaxCents,
            });
        }
    }

    /// <summary>A payload the order service should never send is a permanent failure before any write (00-reliability §7).</summary>
    private static void Validate(OrderReadyToFulfillV1 ready)
    {
        if (ready.Lines is not { Count: > 0 }) throw new PermanentFailureException($"Order {ready.OrderId} has no lines.");
        if (!ShippingOptions.All.Contains(ready.ShippingOption))
            throw new PermanentFailureException($"Order {ready.OrderId} has an unknown shipping option '{ready.ShippingOption}'.");
        foreach (var l in ready.Lines)
        {
            if (!LineKinds.All.Contains(l.Kind)) throw new PermanentFailureException($"Order {ready.OrderId} line {l.LineNo} has an unknown kind '{l.Kind}'.");
            if (l.Quantity <= 0) throw new PermanentFailureException($"Order {ready.OrderId} line {l.LineNo} has quantity {l.Quantity}.");
            if (l.Kind == LineKinds.WarrantyPlan && (l.ParentLineNo is null || l.CoverageMonths is null))
                throw new PermanentFailureException($"Order {ready.OrderId} plan line {l.LineNo} has no parent line or coverage months.");
        }
    }
}
