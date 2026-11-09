using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Launch.Data;

public sealed class LaunchDbContext(DbContextOptions<LaunchDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder mb) => mb.AddBloomReliabilityTables();

    /// <summary>The database's <c>now()</c>, the clock of every check and stored timestamp inside a transaction (plan R6).</summary>
    public Task<DateTimeOffset> NowAsync(CancellationToken ct) =>
        Database.SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"").SingleAsync(ct);
}
