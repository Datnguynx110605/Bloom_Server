using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Identity.Data;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder mb) => mb.AddBloomReliabilityTables();
}
