using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PanelFleet.Client.ContractTests;

/// <summary>收集日志消息用于脱敏断言。</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages, categoryName);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _messages;
        private readonly string _categoryName;

        public CapturingLogger(ConcurrentQueue<string> messages, string categoryName)
        {
            _messages = messages;
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _messages.Enqueue($"{_categoryName}: {formatter(state, exception)}");
        }
    }
}
