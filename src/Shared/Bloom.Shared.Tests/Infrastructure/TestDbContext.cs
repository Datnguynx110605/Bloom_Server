using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Shared.Tests;

public sealed class Counter
{
    public string Id { get; set; } = "";
    public int Held { get; set; }
    public int Allocation { get; set; }
}

public sealed class Item
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public long Version { get; set; }
}

public sealed class Effect
{
    public long Id { get; set; }
    public string Consumer { get; set; } = "";
    public Guid MessageId { get; set; }
}

public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Counter> Counters => Set<Counter>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Effect> Effects => Set<Effect>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Counter>(e =>
        {
            e.ToTable("counters");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Held).HasColumnName("held");
            e.Property(x => x.Allocation).HasColumnName("allocation");
        });
        mb.Entity<Item>(e =>
        {
            e.ToTable("items");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Version).HasColumnName("version").HasDefaultValue(0L);
        });
        mb.Entity<Effect>(e =>
        {
            e.ToTable("effects");
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.Consumer).HasColumnName("consumer");
            e.Property(x => x.MessageId).HasColumnName("message_id");
        });
        mb.AddBloomReliabilityTables();
    }
}
