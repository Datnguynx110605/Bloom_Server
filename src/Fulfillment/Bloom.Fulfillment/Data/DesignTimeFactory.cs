using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bloom.Fulfillment.Data;

/// <summary>Lets <c>dotnet ef</c> build the context without the app's configuration (plan R2). <c>Fulfillment__Database</c> names the target database.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<FulfillmentDbContext>
{
    public FulfillmentDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FulfillmentDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("Fulfillment__Database") ?? "Host=design-time;Database=fulfillment").Options);
}
