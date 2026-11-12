using Bloom.Catalog.Data;
using Bloom.Order.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Consumers;

/// <summary>08-catalog §5.2, plan R24: a delivered order makes the buyer a verified buyer of each physical product in it (plans have no reviews).</summary>
public sealed class OrderDeliveredConsumer(CatalogDbContext db) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("order", OrderMessages.Delivered)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var e = envelope.GetPayload<OrderDeliveredV1>();
        var products = e.ProductIds.Distinct().ToArray();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO review_eligibility (account_id, product_id, first_delivered_at)
            SELECT {e.AccountId}, p.id, {e.DeliveredAt} FROM products p WHERE p.id = ANY({products}) AND p.kind = 'physical'
            ON CONFLICT (account_id, product_id) DO UPDATE
                SET first_delivered_at = LEAST(review_eligibility.first_delivered_at, EXCLUDED.first_delivered_at)
            """, ct);
    }
}
