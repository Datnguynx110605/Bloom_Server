using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Bloom.Catalog.Caching;

/// <summary>
/// The cache entries a unit of work wants deleted (plan R9). Writes register them; <see cref="CacheInvalidationInterceptor"/>
/// deletes them after the transaction commits, and drops them when it rolls back.
/// </summary>
public sealed class CacheInvalidation
{
    private readonly HashSet<Guid> _products = [];
    private bool _categories, _listings;

    public void Product(Guid id) => _products.Add(id);

    public void Categories() => _categories = true;

    /// <summary>Anything that changes what a search result shows: bumps <c>catalog:version</c>.</summary>
    public void Listings() => _listings = true;

    internal (Guid[] Products, bool Categories, bool Listings) Drain()
    {
        var drained = (_products.ToArray(), _categories, _listings);
        _products.Clear();
        _categories = _listings = false;
        return drained;
    }
}

public sealed class CacheInvalidationInterceptor(CacheInvalidation pending, CatalogCache cache) : DbTransactionInterceptor
{
    // CancellationToken.None: a client that gives up after the commit must not leave stale entries behind.
    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        var (products, categories, listings) = pending.Drain();
        if (products.Length > 0 || categories || listings) await cache.InvalidateAsync(products, categories, listings);
    }

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        pending.Drain();
        return Task.CompletedTask;
    }
}
