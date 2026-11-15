using System.Diagnostics;
using System.Text.Json;
using Bloom.Shared.Messaging;
using Bloom.Shared.Observability;
using Bloom.Shared.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bloom.Shared.Tests;

public class OutboxWriterTests(Containers containers)
{
    private static (OutboxWriter Writer, TestDbContext Db, string Service) Create(IServiceScope scope)
    {
        var sp = scope.ServiceProvider;
        return (sp.GetRequiredService<OutboxWriter>(), sp.GetRequiredService<TestDbContext>(),
            sp.GetRequiredService<BloomOptions>().ServiceName);
    }

    private static Dictionary<string, string> Headers(OutboxMessage row) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(row.Headers)!;

    [Fact]
    public async Task T01_rolled_back_transaction_leaves_no_outbox_row()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        using var scope = host.Services.CreateScope();
        var (writer, db, _) = Create(scope);

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            writer.PublishEvent("t.thing-happened.v1", new { a = 1 });
            await db.SaveChangesAsync(ct);
            await tx.RollbackAsync(ct);
        }

        using var check = host.Services.CreateScope();
        Assert.Equal(0, await check.ServiceProvider.GetRequiredService<TestDbContext>().Set<OutboxMessage>().CountAsync(ct));
    }

    [Fact]
    public async Task Committed_event_row_has_envelope_and_columns()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        Guid id;
        string svc;
        using (var scope = host.Services.CreateScope())
        {
            var (writer, db, s) = Create(scope);
            svc = s;
            id = writer.PublishEvent("t.thing-happened.v1", new { a = 1 });
            await db.SaveChangesAsync(ct);
        }

        using var check = host.Services.CreateScope();
        var row = await check.ServiceProvider.GetRequiredService<TestDbContext>().Set<OutboxMessage>().SingleAsync(ct);
        Assert.Equal(id, row.Id);
        Assert.Equal("event", row.Kind);
        Assert.Equal($"{svc}.events", row.Exchange);
        Assert.Equal("t.thing-happened.v1", row.MessageType);
        Assert.Equal(row.MessageType, row.RoutingKey);
        Assert.Equal("pending", row.Status);
        Assert.Equal(0, row.Attempts);
        Assert.True(row.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
        var env = Envelope.Parse(System.Text.Encoding.UTF8.GetBytes(row.Payload));
        Assert.Equal(row.Id, env.MessageId);
        Assert.Equal(svc, env.Producer);
        Assert.Equal(default, env.OccurredAt);
        Assert.Equal("event", env.Kind);
        Assert.Equal(1, env.Payload.GetProperty("a").GetInt32());
        Assert.Null(env.CausationId);
        Assert.Equal(env.CorrelationId, Guid.Parse(Headers(row)["correlation_id"]));
        Assert.False(Headers(row).ContainsKey("causation_id"));
    }

    [Fact]
    public async Task Command_goes_to_target_commands_exchange()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        using (var scope = host.Services.CreateScope())
        {
            var (writer, db, _) = Create(scope);
            writer.SendCommand("inventory", "inventory.reserve-stock.v1", new { sku = "x" });
            await db.SaveChangesAsync(ct);
        }

        using var check = host.Services.CreateScope();
        var row = await check.ServiceProvider.GetRequiredService<TestDbContext>().Set<OutboxMessage>().SingleAsync(ct);
        Assert.Equal("command", row.Kind);
        Assert.Equal("inventory.commands", row.Exchange);
        Assert.Equal("inventory.reserve-stock.v1", row.RoutingKey);
        Assert.Equal("command", Envelope.Parse(System.Text.Encoding.UTF8.GetBytes(row.Payload)).Kind);
    }

    private async Task<(Envelope Env, OutboxMessage Row)> WriteWithRequestId(string? requestId)
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        using var scope = host.Services.CreateScope();
        var (_, db, _) = Create(scope);
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        if (requestId is not null) http.HttpContext.Request.Headers["x-bloom-request-id"] = requestId;
        var writer = new OutboxWriter(db, scope.ServiceProvider.GetRequiredService<BloomOptions>(), http);
        writer.PublishEvent("t.thing-happened.v1", new { a = 1 });
        await db.SaveChangesAsync(ct);
        var row = await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(ct);
        return (Envelope.Parse(System.Text.Encoding.UTF8.GetBytes(row.Payload)), row);
    }

    [Fact]
    public async Task Correlation_comes_from_uuid_request_id()
    {
        var id = Guid.NewGuid();
        var (env, row) = await WriteWithRequestId(id.ToString());
        Assert.Equal(id, env.CorrelationId);
        Assert.Equal(id.ToString(), Headers(row)["correlation_id"]);
    }

    [Fact]
    public async Task Non_uuid_request_id_starts_new_flow()
    {
        var (env, _) = await WriteWithRequestId("abc");
        Assert.NotEqual(Guid.Empty, env.CorrelationId);
        Assert.Equal(7, env.CorrelationId.Version);
    }

    [Fact]
    public async Task Handling_a_message_sets_causation_and_correlation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        using var scope = host.Services.CreateScope();
        var (writer, db, _) = Create(scope);
        var incoming = new Envelope(Guid.CreateVersion7(), "t.in.v1", "event", "other", DateTime.UtcNow, Guid.NewGuid(),
            JsonDocument.Parse("{}").RootElement);

        MessageScope.Current.Value = incoming;
        try { writer.PublishEvent("t.thing-happened.v1", new { a = 1 }); }
        finally { MessageScope.Current.Value = null; }
        await db.SaveChangesAsync(ct);

        var row = await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(ct);
        var env = Envelope.Parse(System.Text.Encoding.UTF8.GetBytes(row.Payload));
        Assert.Equal(incoming.MessageId, env.CausationId);
        Assert.Equal(incoming.CorrelationId, env.CorrelationId);
        Assert.Equal(incoming.MessageId.ToString(), Headers(row)["causation_id"]);
        Assert.Equal(incoming.CorrelationId.ToString(), Headers(row)["correlation_id"]);
    }

    [Fact]
    public async Task Traceparent_copied_from_current_activity()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        using var scope = host.Services.CreateScope();
        var (writer, db, _) = Create(scope);
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Bloom.Shared",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        string? expected;
        using (var activity = BloomActivity.Source.StartActivity("test"))
        {
            expected = activity?.Id;
            writer.PublishEvent("t.thing-happened.v1", new { a = 1 });
        }
        await db.SaveChangesAsync(ct);

        Assert.NotNull(expected);
        var row = await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(ct);
        Assert.Equal(expected, Headers(row)["traceparent"]);
    }

    [Fact]
    public void Invalid_message_type_throws()
    {
        var writer = new OutboxWriter(null!, new BloomOptions { ServiceName = "t" }, new HttpContextAccessor());
        Assert.Throws<ArgumentException>(() => writer.PublishEvent("Bad", new { a = 1 }));
        Assert.Throws<ArgumentException>(() => writer.SendCommand("inventory", "Bad", new { a = 1 }));
    }
}
