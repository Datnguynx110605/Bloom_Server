using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Bloom.Shared.Observability;

/// <summary>Instruments of 00-reliability §12. Every measurement carries <c>service</c> (R10).</summary>
public sealed class BloomMetrics
{
    public const string MeterName = "Bloom.Shared";

    private readonly KeyValuePair<string, object?> _service;
    private readonly Counter<long> _outboxPublish, _consumerMessages, _idempotency, _optimisticConflicts, _conditionalRejections;
    private readonly Histogram<double> _consumerHandle;
    private readonly ConcurrentDictionary<string, long> _dlqDepth = new();
    private long _outboxPending;
    private double _outboxOldestAge;

    public BloomMetrics(IMeterFactory meterFactory, BloomOptions options)
    {
        _service = new("service", options.ServiceName);
        var meter = meterFactory.Create(MeterName);
        _outboxPublish = meter.CreateCounter<long>("outbox_publish_total");
        _consumerMessages = meter.CreateCounter<long>("consumer_messages_total");
        _idempotency = meter.CreateCounter<long>("idempotency_requests_total");
        _optimisticConflicts = meter.CreateCounter<long>("optimistic_conflicts_total");
        _conditionalRejections = meter.CreateCounter<long>("conditional_update_rejections_total");
        _consumerHandle = meter.CreateHistogram<double>("consumer_handle_seconds", "s",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10] });
        meter.CreateObservableGauge("outbox_pending_count", () => new Measurement<long>(Interlocked.Read(ref _outboxPending), _service));
        meter.CreateObservableGauge("outbox_oldest_pending_age_seconds", () => new Measurement<double>(Volatile.Read(ref _outboxOldestAge), _service));
        meter.CreateObservableGauge("dlq_depth", () => _dlqDepth.Select(d => new Measurement<long>(d.Value, _service, new("queue", d.Key))).ToArray());
    }

    internal void OutboxPublish(string result, int count = 1) => _outboxPublish.Add(count, _service, new("result", result));

    internal void ConsumerMessage(string consumer, string result) =>
        _consumerMessages.Add(1, _service, new("consumer", consumer), new("result", result));

    internal void ConsumerHandle(string consumer, double seconds) =>
        _consumerHandle.Record(seconds, _service, new("consumer", consumer));

    internal void Idempotency(string operation, string result) =>
        _idempotency.Add(1, _service, new("operation", operation), new("result", result));

    internal void OptimisticConflict(string entity) => _optimisticConflicts.Add(1, _service, new("entity", entity));

    internal void ConditionalRejection(string counter) => _conditionalRejections.Add(1, _service, new("counter", counter));

    internal void ReportOutboxBacklog(long count, double oldestAgeSeconds)
    {
        Interlocked.Exchange(ref _outboxPending, count);
        Volatile.Write(ref _outboxOldestAge, oldestAgeSeconds);
    }

    internal void ReportDlqDepth(string queue, long depth) => _dlqDepth[queue] = depth;
}
