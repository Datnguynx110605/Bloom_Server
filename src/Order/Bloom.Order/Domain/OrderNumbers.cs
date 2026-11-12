using System.Security.Cryptography;
using Bloom.Order.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bloom.Order.Domain;

/// <summary>Order numbers (plan R11): <c>BLM-</c> and 8 characters of Crockford base32.</summary>
public static class OrderNumbers
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string UniqueIndex = "ux_orders_number";
    private const string Savepoint = "order_number";

    public static string Next() => "BLM-" + RandomNumberGenerator.GetString(Alphabet, 8);

    /// <summary>
    /// Gives <paramref name="order"/> a number and saves it in the open transaction. On a number already taken the insert is rolled back to a savepoint and
    /// tried again with a new number. Nothing else may be pending in the context: it is saved with the order.
    /// </summary>
    public static async Task InsertAsync(OrderDbContext db, OrderRow order, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction ?? throw new InvalidOperationException("The order number retry needs an open transaction.");
        db.Orders.Add(order);
        while (true)
        {
            order.Number = Next();
            await tx.CreateSavepointAsync(Savepoint, ct);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: UniqueIndex })
            {
                await tx.RollbackToSavepointAsync(Savepoint, ct);
            }
        }
    }
}
