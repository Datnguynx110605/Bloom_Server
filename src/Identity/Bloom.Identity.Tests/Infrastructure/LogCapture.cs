using Microsoft.Extensions.Logging;

namespace Bloom.Identity.Tests;

/// <summary>Records every log entry (category, level, formatted message with exception, state values) for assertions.</summary>
public sealed class LogCapture : ILoggerProvider
{
    public sealed record Entry(string Category, LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State);

    private readonly List<Entry> _entries = [];

    public IReadOnlyList<Entry> Entries
    {
        get { lock (_entries) return [.. _entries]; }
    }

    public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, this);

    public void Dispose() { }

    private sealed class Recorder(string category, LogCapture capture) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            var entry = new Entry(category, logLevel, formatter(state, exception) + exception, [.. values]);
            lock (capture._entries) capture._entries.Add(entry);
        }
    }
}
