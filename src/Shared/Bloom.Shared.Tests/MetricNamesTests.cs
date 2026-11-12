using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Bloom.Shared.Observability;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;

namespace Bloom.Shared.Tests;

public class MetricNamesTests
{
    // Same exact tag as the Compose file (plan Task 11).
    private const string CollectorImage = "otel/opentelemetry-collector-contrib:0.140.0";

    [Fact]
    public async Task Prometheus_names_match_section_12()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var collector = new ContainerBuilder(CollectorImage)
            .WithResourceMapping(File.ReadAllBytes(CollectorConfig()), "/etc/otelcol-contrib/config.yaml")
            .WithPortBinding(4318, true)
            .WithPortBinding(8889, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Everything is ready"))
            .Build();
        await collector.StartAsync(ct);

        using var meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
        using (var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(BloomMetrics.MeterName)
            .AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri($"http://{collector.Hostname}:{collector.GetMappedPublicPort(4318)}/v1/metrics");
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
            })
            .Build())
        {
            var metrics = new BloomMetrics(meters.GetRequiredService<IMeterFactory>(), new BloomOptions { ServiceName = "svc" });
            metrics.OutboxPublish("confirmed");
            metrics.ConsumerMessage("c", "ack");
            metrics.ConsumerHandle("c", 0.01);
            metrics.Idempotency("op", "hit");
            metrics.OptimisticConflict("e");
            metrics.ConditionalRejection("k");
            metrics.ReportOutboxBacklog(3, 10);
            metrics.ReportDlqDepth("q", 2);
            Assert.True(provider.ForceFlush(10_000));
        }

        using var http = new HttpClient();
        var url = $"http://{collector.Hostname}:{collector.GetMappedPublicPort(8889)}/metrics";
        string[] lines = [];
        string[][] expected =
        [
            ["outbox_pending_count"], ["outbox_oldest_pending_age_seconds"], ["outbox_publish_total", "result=\"confirmed\""],
            ["consumer_messages_total", "consumer=\"c\"", "result=\"ack\""], ["consumer_handle_seconds_bucket", "consumer=\"c\""],
            ["dlq_depth", "queue=\"q\""], ["idempotency_requests_total", "operation=\"op\"", "result=\"hit\""],
            ["optimistic_conflicts_total", "entity=\"e\""], ["conditional_update_rejections_total", "counter=\"k\""],
        ];
        bool Has(string[] e) => lines.Any(l => l.StartsWith(e[0] + "{") && l.Contains("service=\"svc\"") && e[1..].All(l.Contains));
        await Poll.UntilAsync(async () =>
        {
            try { lines = (await http.GetStringAsync(url, ct)).Split('\n'); }
            catch (HttpRequestException) { /* exporter not listening yet */ }
            return expected.All(Has);
        }, 30, () => "metric " + string.Join(" / ", expected.First(e => !Has(e))) + " on the Prometheus endpoint");
        Assert.DoesNotContain(lines, l => l.StartsWith("outbox_publish_total_total") || l.StartsWith("consumer_handle_seconds_seconds"));
    }

    private static string CollectorConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "deploy", "compose", "otel-collector.yaml"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new FileNotFoundException("deploy/compose/otel-collector.yaml"), "deploy", "compose", "otel-collector.yaml");
    }
}
