using Bloom.Fulfillment.Consumers;
using Bloom.Fulfillment.Data;
using Bloom.Order.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Tests;

/// <summary>Order-side events and direct row writes for tests.</summary>
public static class Seed
{
    public sealed class ReadyBuilder
    {
        internal readonly List<ReadyLineV1> Lines =
        [
            Make(1, "product", serialized: true, quantity: 2, paid: 2002, tax: 160),
            Make(2, "warranty_plan", quantity: 1, parent: 1, months: 12, paid: 9900, tax: 800),
        ];

        internal string Option = "standard";
        internal long ShippingCents;
        internal Guid AccountId = Guid.NewGuid();

        /// <summary>Adds the line, or replaces the default line with the same number.</summary>
        public ReadyBuilder Line(int lineNo, string kind = "product", bool serialized = false, int quantity = 1, int? parent = null, int? months = null, long paid = 1000, long tax = 80)
        {
            Lines.RemoveAll(l => l.LineNo == lineNo);
            Lines.Add(Make(lineNo, kind, serialized, quantity, parent, months, paid, tax));
            return this;
        }

        public ReadyBuilder Express(long cents)
        {
            Option = "express";
            ShippingCents = cents;
            return this;
        }

        public ReadyBuilder Account(Guid accountId)
        {
            AccountId = accountId;
            return this;
        }

        private static ReadyLineV1 Make(int lineNo, string kind, bool serialized = false, int quantity = 1, int? parent = null, int? months = null, long paid = 1000, long tax = 80) =>
            new(lineNo, kind, Guid.CreateVersion7(), Guid.CreateVersion7(), serialized, quantity, parent, months, paid, tax);
    }

    /// <summary>A valid <c>order.ready-to-fulfill.v1</c>: a serialized device line 1 (2 units) and its 12-month plan line 2, standard shipping at 0 cents.</summary>
    public static OrderReadyToFulfillV1 Ready(Action<ReadyBuilder>? configure = null)
    {
        var b = new ReadyBuilder();
        configure?.Invoke(b);
        var lines = b.Lines.OrderBy(l => l.LineNo).ToList();
        return new OrderReadyToFulfillV1(
            Guid.NewGuid(), b.AccountId, "BL-1001", new ShipToV1("Ann Lee", ["1 Main St", "Apt 2"], "Reno", "NV", "89501", "+17755550123"),
            b.Option, b.ShippingCents, lines.Sum(l => l.PaidCents) + b.ShippingCents, lines);
    }

    /// <summary>Consumes the event the way <c>ConsumerHost</c> does.</summary>
    public static Task ReadyAsync(TestFulfillment t, OrderReadyToFulfillV1 ready) =>
        Consume.Async<ReadyToFulfillConsumer>(t, OrderMessages.ReadyToFulfill, ready);

    public static async Task<PackTask?> TaskAsync(TestFulfillment t, Guid orderId)
    {
        await using var db = t.NewDb();
        return await db.PackTasks.AsNoTracking().SingleOrDefaultAsync(p => p.RefId == orderId, TestContext.Current.CancellationToken);
    }

    public static async Task SetTaskStatusAsync(TestFulfillment t, Guid orderId, string status)
    {
        await using var db = t.NewDb();
        await db.PackTasks.Where(p => p.RefId == orderId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, status), TestContext.Current.CancellationToken);
    }
}
