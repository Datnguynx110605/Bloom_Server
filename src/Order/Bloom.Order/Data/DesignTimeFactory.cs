using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bloom.Order.Data;

/// <summary>Lets <c>dotnet ef</c> build the context without the app's configuration (plan R2). <c>Order__Database</c> names the target database.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<OrderDbContext>
{
    public OrderDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("Order__Database") ?? "Host=design-time;Database=order").Options);
}
