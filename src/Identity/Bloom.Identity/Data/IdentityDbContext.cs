using System.Text.RegularExpressions;
using Bloom.Shared.Persistence;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Bloom.Identity.Data;

public sealed partial class IdentityDbContext(DbContextOptions<IdentityDbContext> options, TimeProvider clock)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<VerificationCode> VerificationCodes => Set<VerificationCode>();
    public DbSet<StaffInvitation> StaffInvitations => Set<StaffInvitation>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    private static string In(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.AddBloomReliabilityTables();

        mb.Entity<Account>(e =>
        {
            e.ToTable("accounts", t =>
            {
                t.HasCheckConstraint("ck_accounts_type", $"type IN ({In([AccountTypes.Customer, AccountTypes.Staff])})");
                t.HasCheckConstraint("ck_accounts_status", $"status IN ({In([AccountStatuses.Active, AccountStatuses.Deactivated, AccountStatuses.Deleted])})");
                t.HasCheckConstraint("ck_accounts_role", $"role IS NULL OR role IN ({In(StaffRoles.All)})");
            });
            e.HasIndex(x => x.EmailNormalized).IsUnique().HasDatabaseName("ux_accounts_email_normalized");
            e.HasIndex(x => x.GoogleSub).IsUnique().HasFilter("google_sub IS NOT NULL").HasDatabaseName("ux_accounts_google_sub");
        });

        mb.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens", t => t.HasCheckConstraint("ck_refresh_tokens_status",
                $"status IN ({In([RefreshStatuses.Active, RefreshStatuses.Rotated, RefreshStatuses.Revoked])})"));
            e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_refresh_tokens_token_hash");
            e.HasIndex(x => x.FamilyId).HasDatabaseName("ix_refresh_tokens_family_id");
            e.HasIndex(x => x.AccountId).HasFilter("status = 'active'").HasDatabaseName("ix_refresh_tokens_account_active");
        });

        mb.Entity<VerificationCode>(e =>
        {
            e.ToTable("verification_codes", t => t.HasCheckConstraint("ck_verification_codes_purpose",
                $"purpose IN ({In([CodePurposes.SignUp, CodePurposes.ResetPassword, CodePurposes.ChangeEmail])})"));
            e.HasIndex(x => new { x.TargetEmail, x.Purpose }).HasFilter("consumed_at IS NULL").HasDatabaseName("ix_verification_codes_open");
        });

        mb.Entity<StaffInvitation>(e =>
        {
            e.ToTable("staff_invitations", t => t.HasCheckConstraint("ck_staff_invitations_role", $"role IN ({In(StaffRoles.All)})"));
            e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_staff_invitations_token_hash");
        });

        mb.Entity<Address>(e =>
        {
            e.ToTable("addresses");
            e.HasIndex(x => x.AccountId).HasDatabaseName("ix_addresses_account_id");
        });

        mb.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log");
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.HasIndex(x => x.OccurredAt).HasDatabaseName("ix_audit_log_occurred_at");
            e.HasIndex(x => new { x.ActorId, x.OccurredAt }).HasDatabaseName("ix_audit_log_actor");
            e.HasIndex(x => new { x.TargetType, x.TargetId }).HasDatabaseName("ix_audit_log_target");
        });

        mb.Entity<DataProtectionKey>().ToTable("data_protection_keys");

        // Ids come from the app (UUIDv7); columns are snake_case. The reliability tables name their own columns.
        foreach (var entity in mb.Model.GetEntityTypes().Where(t => t.ClrType.Namespace == typeof(Account).Namespace
                     || t.ClrType == typeof(DataProtectionKey)))
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(SnakeCase().Replace(property.Name, "_").ToLowerInvariant());
            if (entity.ClrType.Namespace == typeof(Account).Namespace)
                entity.FindProperty("Id")!.ValueGenerated = ValueGenerated.Never;
        }
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries<Account>().Where(e => e.State == EntityState.Modified))
        {
            entry.Entity.Version++;
            entry.Entity.UpdatedAt = now;
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex SnakeCase();
}
