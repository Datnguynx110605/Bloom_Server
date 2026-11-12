using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bloom.Identity.Data;

/// <summary>Lets <c>dotnet ef</c> build the context without a database or the app's configuration (plan I2).</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql("Host=design-time;Database=identity").Options, TimeProvider.System);
}
