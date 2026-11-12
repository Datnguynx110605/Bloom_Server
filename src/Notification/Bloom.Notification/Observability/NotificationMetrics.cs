using System.Diagnostics.Metrics;

namespace Bloom.Notification.Observability;

/// <summary>Instruments of 09-notification §10; labels per plan N13.</summary>
public sealed class NotificationMetrics
{
    public const string MeterName = "Bloom.Notification";

    private static readonly KeyValuePair<string, object?> Pod = new("pod", Environment.MachineName);

    private readonly Counter<long> _created, _pushed, _rejected;
    private readonly UpDownCounter<long> _connections;
    private readonly Histogram<double> _emailSend, _eventToEmail;
    private IReadOnlyDictionary<string, long> _jobs = new Dictionary<string, long>();

    public NotificationMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _created = meter.CreateCounter<long>("notifications_created_total");
        _pushed = meter.CreateCounter<long>("ws_pushes_total");
        _rejected = meter.CreateCounter<long>("ws_rejected_total");
        _connections = meter.CreateUpDownCounter<long>("ws_connections");
        _emailSend = meter.CreateHistogram<double>("email_send_seconds", "s");
        _eventToEmail = meter.CreateHistogram<double>("event_to_email_seconds", "s");
        meter.CreateObservableGauge("email_jobs_by_status", () => _jobs.Select(j => new Measurement<long>(j.Value, Tag("status", j.Key))));
    }

    /// <param name="channel"><c>in_app</c> or <c>email</c>.</param>
    public void Created(string type, string channel) => _created.Add(1, Tag("type", type), Tag("channel", channel));

    public void Pushed(string type) => _pushed.Add(1, Tag("type", type));

    /// <param name="reason"><c>ticket</c> or <c>origin</c>.</param>
    public void Rejected(string reason) => _rejected.Add(1, Tag("reason", reason));

    public void ConnectionOpened() => _connections.Add(1, Pod);

    public void ConnectionClosed() => _connections.Add(-1, Pod);

    public void EmailSend(double seconds) => _emailSend.Record(seconds);

    public void EventToEmail(string type, double seconds) => _eventToEmail.Record(seconds, Tag("type", type));

    public void SetEmailJobs(IReadOnlyDictionary<string, long> byStatus) => _jobs = byStatus;

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);
}
