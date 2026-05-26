using System.Collections.Concurrent;

namespace LaadinfrastructuurPlanner.Services;

public sealed record RecentLogEntry(DateTime WhenUtc, string Level, string Category, string Message, string? Exception);

public sealed class RecentExceptionBuffer
{
    private const int MaxEntries = 50;
    private readonly ConcurrentQueue<RecentLogEntry> _entries = new();

    public void Add(RecentLogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }
    }

    public RecentLogEntry[] Snapshot() => _entries.ToArray();
}

public sealed class RecentExceptionLoggerProvider : ILoggerProvider
{
    private readonly RecentExceptionBuffer _buffer;

    public RecentExceptionLoggerProvider(RecentExceptionBuffer buffer)
    {
        _buffer = buffer;
    }

    public ILogger CreateLogger(string categoryName) => new BufferLogger(categoryName, _buffer);

    public void Dispose() { }

    private sealed class BufferLogger : ILogger
    {
        private readonly string _category;
        private readonly RecentExceptionBuffer _buffer;

        public BufferLogger(string category, RecentExceptionBuffer buffer)
        {
            _category = category;
            _buffer = buffer;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) && exception is null) return;
            _buffer.Add(new RecentLogEntry(
                DateTime.UtcNow,
                logLevel.ToString(),
                _category,
                formatter(state, exception),
                exception?.ToString()));
        }
    }
}
