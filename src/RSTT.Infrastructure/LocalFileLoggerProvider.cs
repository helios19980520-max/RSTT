using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;

namespace RSTT.Infrastructure;

public sealed class LocalFileLoggerProvider : ILoggerProvider
{
    private readonly IAppPaths _paths;
    private readonly object _gate = new();

    public LocalFileLoggerProvider(IAppPaths paths)
    {
        _paths = paths;
        _paths.EnsureDirectoriesExist();
    }

    public ILogger CreateLogger(string categoryName) => new LocalFileLogger(_paths, categoryName, _gate);

    public void Dispose()
    {
    }

    private sealed class LocalFileLogger : ILogger
    {
        private readonly IAppPaths _paths;
        private readonly string _categoryName;
        private readonly object _gate;

        public LocalFileLogger(IAppPaths paths, string categoryName, object gate)
        {
            _paths = paths;
            _categoryName = categoryName;
            _gate = gate;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var timestamp = DateTimeOffset.Now;
            var line = $"{timestamp:O} [{logLevel}] {_categoryName}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            var filePath = Path.Combine(_paths.LogsDirectory, $"rstt-{timestamp:yyyyMMdd}.log");
            lock (_gate)
            {
                File.AppendAllText(filePath, line + Environment.NewLine);
            }
        }
    }
}
