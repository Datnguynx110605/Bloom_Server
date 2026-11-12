using Bloom.Catalog.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Domain;

/// <summary>Derived product columns of plan R12, recomputed in the caller's transaction.</summary>
public sealed class ProductDerived(CatalogDbContext db)
{
    /// <summary>
    /// Recomputes <c>min_price_cents</c> (active SKUs, null when none) and <c>search_vector</c> (name A, option values B, category name B,
    /// description C; English) of the given products in one statement. Call it after any write that changes a price, a SKU's
    /// <c>active</c>, the name, the options, the category (all its products on rename) or the description.
    /// </summary>
    public async Task RefreshAsync(params Guid[] productIds)
    {
        if (productIds.Length == 0) return;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE products p SET
              min_price_cents = (SELECT min(s.price_cents) FROM skus s WHERE s.product_id = p.id AND s.active),
              search_vector =
                setweight(to_tsvector('english', p.name), 'A') ||
                setweight(to_tsvector('english', COALESCE((
                  SELECT string_agg(v.value, ' ' ORDER BY t.position, v.position)
                  FROM option_types t JOIN option_values v ON v.option_type_id = t.id
                  WHERE t.product_id = p.id), '')), 'B') ||
                setweight(to_tsvector('english', (SELECT c.name FROM categories c WHERE c.id = p.category_id)), 'B') ||
                setweight(to_tsvector('english', p.description_md), 'C')
            WHERE p.id = ANY({productIds})
            """);
    }
}
