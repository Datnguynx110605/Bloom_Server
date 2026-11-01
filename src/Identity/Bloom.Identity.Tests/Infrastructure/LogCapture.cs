using Microsoft.Extensions.Logging;

namespace Bloom.Identity.Tests;

/// <summary>Records every log entry (category, level, formatted message with exception, state and scope values) for assertions.</summary>
public sealed class LogCapture : ILoggerProvider, ISupportExternalScope
{
    public sealed record Entry(string Category, LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State);

    private readonly List<Entry> _entries = [];
    private IExternalScopeProvider? _scopes;

    public IReadOnlyList<Entry> Entries
    {
        get { lock (_entries) return [.. _entries]; }
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, this);

    public void Dispose() { }

    private sealed class Recorder(string category, LogCapture capture) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => capture._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = new List<KeyValuePair<string, object?>>(state as IEnumerable<KeyValuePair<string, object?>> ?? []);
            capture._scopes?.ForEachScope((scope, list) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> pairs) list.AddRange(pairs);
                else list.Add(new("scope", scope));
            }, values);
            var entry = new Entry(category, logLevel, formatter(state, exception) + exception, values);
            lock (capture._entries) capture._entries.Add(entry);
        }
    }
}
