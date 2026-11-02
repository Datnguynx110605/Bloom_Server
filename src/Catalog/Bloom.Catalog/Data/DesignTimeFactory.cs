using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bloom.Catalog.Data;

/// <summary>Lets <c>dotnet ef</c> build the context without a database or the app's configuration (plan R2).</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql("Host=design-time;Database=catalog").Options);
}
