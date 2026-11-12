using System.Security.Cryptography;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Identity.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Identity.Tests;

/// <summary>Rows written straight to the database, the way later tasks' tests need their starting state.</summary>
public static class Seed
{
    public const string Password = "Correct-horse-42";

    public static async Task<Account> CustomerAsync(TestIdentity t, string? email = null, string? password = Password, string? googleSub = null)
    {
        email ??= $"{Guid.NewGuid():N}@example.com";
        var now = t.Clock.GetUtcNow();
        var account = new Account
        {
            Id = Guid.CreateVersion7(),
            Type = AccountTypes.Customer,
            EmailNormalized = email.Trim().ToLowerInvariant(),
            EmailDisplay = email.Trim(),
            PasswordHash = password is null ? null : Hash(t, password),
            GoogleSub = googleSub,
            Name = "Test Customer",
            Status = AccountStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await Save(t, account);
        return account;
    }

    public static async Task<(Account Account, byte[] TotpSecret)> StaffAsync(
        TestIdentity t, string role = StaffRoles.Support, string? password = Password, bool withTotp = true, string status = AccountStatuses.Active)
    {
        var email = $"{Guid.NewGuid():N}@bloom.example";
        var now = t.Clock.GetUtcNow();
        var secret = withTotp ? RandomNumberGenerator.GetBytes(20) : [];
        var account = new Account
        {
            Id = Guid.CreateVersion7(),
            Type = AccountTypes.Staff,
            EmailNormalized = email,
            EmailDisplay = email,
            PasswordHash = password is null ? null : Hash(t, password),
            Name = "Test Staff",
            Role = role,
            TotpSecretEnc = withTotp ? t.Services.GetRequiredKeyedService<SecretBox>("totp").Seal(secret) : null,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await Save(t, account);
        return (account, secret);
    }

    /// <summary>A new refresh-token family for the account, as a sign-in would start it.</summary>
    public static async Task<IssuedSession> SessionAsync(TestIdentity t, Account a)
    {
        await using var scope = t.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var account = await db.Accounts.FindAsync([a.Id], TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Seed the account first.");
        var session = await scope.ServiceProvider.GetRequiredService<SessionService>().StartAsync(account);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return session;
    }

    private static string Hash(TestIdentity t, string password) =>
        t.Services.GetRequiredService<PasswordHasher>().Hash(t.Services.GetRequiredService<PasswordPolicy>().Normalize(password));

    private static async Task Save(TestIdentity t, Account account)
    {
        await using var db = t.NewDb();
        db.Accounts.Add(account);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
