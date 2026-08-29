using Microsoft.Extensions.Logging;
using System.IO;

namespace MsfsAiAtc;

/// <summary>
/// Minimal file logger — writes all log entries to a plain text file.
/// This lets us debug crashes on the end-user's machine without attaching a debugger.
/// The log file is airatc.log in the app root folder.
/// </summary>
public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public FileLoggerProvider(string filePath)
    {
        _filePath = filePath;
        // Write header on each run so entries from different sessions are separated
        var header = $"\n{'=',60}\n  MSFS AI ATC  |  Started {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{'=',60}\n";
        try { File.AppendAllText(_filePath, header); } catch { }
    }

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(_filePath, categoryName, _lock);

    public void Dispose() { }
}

public class FileLogger : ILogger
{
    private readonly string _filePath;
    private readonly string _category;
    private readonly object _lock;

    public FileLogger(string filePath, string category, object lockObj)
    {
        _filePath = filePath;
        _category = category.Split('.').Last(); // Short class name only
        _lock = lockObj;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var level = logLevel switch
        {
            LogLevel.Information => "INFO ",
            LogLevel.Warning     => "WARN ",
            LogLevel.Error       => "ERROR",
            LogLevel.Critical    => "CRIT ",
            _                    => "DEBUG",
        };

        var line = $"[{DateTime.Now:HH:mm:ss}] {level} [{_category}] {formatter(state, exception)}";
        if (exception != null)
            line += $"\n  Exception: {exception}";

        lock (_lock)
        {
            try { File.AppendAllText(_filePath, line + "\n"); } catch { }
        }
    }
}
