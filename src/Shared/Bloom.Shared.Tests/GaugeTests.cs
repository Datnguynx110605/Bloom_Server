using System.Diagnostics.Metrics;
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

        var seen = new Dictionary<string, (object Value, object? Queue)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == BloomMetrics.MeterName) l.EnableMeasurementEvents(i); };
        void OnMeasurement<T>(Instrument i, T v, ReadOnlySpan<KeyValuePair<string, object?>> tags) where T : struct
        {
            var d = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            if (Equals(d.GetValueOrDefault("service"), service)) seen[i.Name] = (v, d.GetValueOrDefault("queue"));
        }
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => OnMeasurement(i, v, tags));
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => OnMeasurement(i, v, tags));
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Equal(3L, seen["outbox_pending_count"].Value);
        Assert.True((double)seen["outbox_oldest_pending_age_seconds"].Value >= 10);
        Assert.Equal((2L, (object?)dlq), seen["dlq_depth"]);
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

        var depths = new Dictionary<object, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Name == "dlq_depth") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, v, tags, _) =>
        {
            var d = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            if (Equals(d["service"], service)) depths[d["queue"]!] = v;
        });
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Equal(0L, depths[$"{service}.h1.dlq"]);
        Assert.Equal(1L, depths[$"{service}.h2.dlq"]);
    }
}
