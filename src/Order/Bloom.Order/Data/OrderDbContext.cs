using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Data;

/// <summary>The reliability tables only; order's own tables arrive with the schema task.</summary>
public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder mb) => mb.AddBloomReliabilityTables();
}
