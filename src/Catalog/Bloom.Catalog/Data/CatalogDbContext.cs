using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Data;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder mb) => mb.AddBloomReliabilityTables();
}
