using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bloom.Launch.Data;

/// <summary>Lets <c>dotnet ef</c> build the context without the app's configuration (plan R2). <c>Launch__Database</c> names the target database.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<LaunchDbContext>
{
    public LaunchDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LaunchDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("Launch__Database") ?? "Host=design-time;Database=launch").Options);
}
