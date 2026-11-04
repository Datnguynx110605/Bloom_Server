using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bloom.Inventory.Data;

/// <summary>Lets <c>dotnet ef</c> build the context without the app's configuration (plan R2). Migrations run as the database owner: <c>Inventory__MigrationDatabase</c> names it.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("Inventory__MigrationDatabase") ?? "Host=design-time;Database=inventory").Options);
}
