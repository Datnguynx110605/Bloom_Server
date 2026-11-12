using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Bloom.Launch.Tests;

/// <summary>Records every log message (<c>[Level] </c> prefix, formatted, with its exception) for assertions.</summary>
public sealed class LogCapture : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => this;

    public void Dispose() { }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue($"[{logLevel}] {formatter(state, exception)}{exception}");
}
