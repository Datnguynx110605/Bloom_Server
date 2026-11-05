using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Data;

public sealed class FulfillmentDbContext(DbContextOptions<FulfillmentDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder mb) => mb.AddBloomReliabilityTables();
}
