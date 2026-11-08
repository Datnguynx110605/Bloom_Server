using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bloom.Payment.Data;

/// <summary>Lets <c>dotnet ef</c> build the context without the app's configuration (plan R2). <c>Payment__Database</c> names the target database.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("Payment__Database") ?? "Host=design-time;Database=payment").Options);
}
