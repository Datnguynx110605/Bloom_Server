using Bloom.Notification.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bloom.Notification.Tests;

public sealed class TestNotificationOptions
{
    public string Mode { get; set; } = "api";

    /// <summary>Reuses this database (for example an API host and a worker host on one database) instead of creating one.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Config keys (for example <c>Notification:StorefrontBaseUrl</c>) that replace the defaults of <see cref="TestNotification.DefaultConfig"/>.</summary>
    public Dictionary<string, string?> Config { get; } = [];
}

/// <summary>The notification app built through <see cref="NotificationApp.Build"/> on <c>TestServer</c> (plan N1), on its own database (plan N11).</summary>
public sealed class TestNotification : IAsyncDisposable
{
    /// <summary>A fixed 32-byte test key, base64.</summary>
    public static readonly string CodeKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private readonly WebApplication _app;

    private TestNotification(WebApplication app) => _app = app;

    public required HttpClient Http { get; init; }

    /// <summary>The app's <see cref="TimeProvider"/>. Starts at the real time.</summary>
    public required FakeTimeProvider Clock { get; init; }

    public required LogCapture Logs { get; init; }

    /// <summary>The connection string of the database (the app connects as its owner, plan N2).</summary>
    public required string ConnectionString { get; init; }

    public required TestServer Server { get; init; }

    public IServiceProvider Services => _app.Services;

    public static async Task<TestNotification> StartAsync(Containers containers, Action<TestNotificationOptions>? configure = null)
    {
        var options = new TestNotificationOptions();
        configure?.Invoke(options);
        var fresh = options.ConnectionString is null;
        var connectionString = options.ConnectionString ?? await containers.CreateDatabaseAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logs = new LogCapture();

        var config = DefaultConfig(connectionString, containers.RabbitMqUri, containers.RedisConnectionString, containers.SmtpHost, containers.SmtpPort.ToString());
        config["Notification:Mode"] = options.Mode;
        foreach (var (key, value) in options.Config) config[key] = value;

        var app = NotificationApp.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(config);
            b.Services.AddSingleton<TimeProvider>(clock); // registered first, so the app's TryAdd keeps it
            b.Services.AddSingleton<ILoggerProvider>(logs);
        });
        if (fresh)
        {
            await using var db = NewDb(connectionString);
            await db.Database.MigrateAsync(); // the app never migrates (plan N2)
        }
        await app.StartAsync();
        return new TestNotification(app)
        {
            Http = app.GetTestClient(),
            Clock = clock,
            Logs = logs,
            ConnectionString = connectionString,
            Server = app.GetTestServer(),
        };
    }

    /// <summary>Every key of plan N4 except <c>Mode</c>, with valid values.</summary>
    internal static Dictionary<string, string?> DefaultConfig(string database, string rabbitMq, string redis, string smtpHost, string smtpPort) => new()
    {
        ["Notification:Database"] = database,
        ["Notification:RabbitMq"] = rabbitMq,
        ["Notification:Redis"] = redis,
        ["Notification:StorefrontBaseUrl"] = "https://shop.test",
        ["Notification:CodeEncryptionKey"] = CodeKey,
        ["Notification:AllowedOrigins:0"] = "https://shop.test",
        ["Notification:Email:Provider"] = "resend",
        ["Notification:Email:From"] = "Bloom <no-reply@bloom.test>",
        ["Notification:Email:Resend:ApiKey"] = "re_test",
        ["Notification:Email:Resend:BaseUrl"] = "https://resend.test/",
        ["Notification:Email:Smtp:Host"] = smtpHost,
        ["Notification:Email:Smtp:Port"] = smtpPort,
    };

    /// <summary>A test-side context on the same database.</summary>
    public NotificationDbContext NewDb() => NewDb(ConnectionString);

    private static NotificationDbContext NewDb(string connectionString) =>
        new(new DbContextOptionsBuilder<NotificationDbContext>().UseNpgsql(connectionString).Options);

    public async Task SqlAsync(FormattableString sql)
    {
        await using var db = NewDb();
        await db.Database.ExecuteSqlAsync(sql, TestContext.Current.CancellationToken);
    }

    public int ErrorLogs => Logs.Entries.Count(e => e.StartsWith("[Error]", StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _app.DisposeAsync();
    }
}
