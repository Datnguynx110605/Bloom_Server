using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bloom.Notification.Data;

/// <summary>Lets <c>dotnet ef</c> build the context without the app's configuration (plan N2). <c>Notification__Database</c> names the target database.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<NotificationDbContext>
{
    public NotificationDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<NotificationDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("Notification__Database") ?? "Host=design-time;Database=notification").Options);
}
