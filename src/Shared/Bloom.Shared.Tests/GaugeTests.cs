using System.Text;
using Bloom.Shared.Messaging;
using Bloom.Shared.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bloom.Shared.Tests;

public class GaugeTests(Containers containers)
{
    [Fact]
    public async Task Gauges_report_backlog_and_dlq_depth()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers, b =>
            b.AddWorker().AddConsumer<RecordingHandler>("h", ConsumerBinding.Event("p", "p.thing.v1")));
        var service = host.Services.GetRequiredService<BloomOptions>().ServiceName;
        var dlq = $"{service}.h.dlq";
        await host.Services.GetServices<IHostedService>().OfType<TopologyInitializer>().Single().StartAsync(ct);
        await using var rabbit = await Rabbit.ConnectAsync(containers);
        for (var i = 0; i < 2; i++) await rabbit.PublishAsync("", dlq, Encoding.UTF8.GetBytes("{}"));
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.ExecuteSqlAsync($"""
                INSERT INTO outbox_messages (id, message_type, kind, exchange, routing_key, payload, headers, status, created_at)
                SELECT gen_random_uuid(), 't.x.v1', 'event', 'e', 'k', '[]', '[]', 'pending', now() - interval '10 seconds' + n * interval '1 second'
                FROM generate_series(0, 2) n
                """, ct);

        await host.Services.GetServices<IHostedService>().OfType<GaugePoller>().Single().PollOnceAsync(ct);

        using var capture = new MetricCapture(service);

        Assert.Equal(3, capture.Gauge("outbox_pending_count")[""]);
        Assert.True(capture.Gauge("outbox_oldest_pending_age_seconds")[""] >= 10);
        Assert.Equal(2, capture.Gauge("dlq_depth")[dlq]);
    }

    [Fact]
    public async Task Missing_dlq_reports_zero_and_next_queue_is_still_polled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers, b => b.AddWorker()
            .AddConsumer<RecordingHandler>("h1", ConsumerBinding.Event("p", "p.thing.v1"))
            .AddConsumer<SecondRecordingHandler>("h2", ConsumerBinding.Event("p", "p.thing.v1")));
        var service = host.Services.GetRequiredService<BloomOptions>().ServiceName;
        await using var rabbit = await Rabbit.ConnectAsync(containers);
        await rabbit.Channel.QueueDeclareAsync($"{service}.h2.dlq", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await rabbit.PublishAsync("", $"{service}.h2.dlq", Encoding.UTF8.GetBytes("{}"));

        await host.Services.GetServices<IHostedService>().OfType<GaugePoller>().Single().PollOnceAsync(ct);

        using var capture = new MetricCapture(service);
        var depths = capture.Gauge("dlq_depth");

        Assert.Equal(0, depths[$"{service}.h1.dlq"]);
        Assert.Equal(1, depths[$"{service}.h2.dlq"]);
    }
}
