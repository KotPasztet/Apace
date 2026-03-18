using Serilog;
using Serilog.Configuration;
using Serilog.Core;

namespace ViennaDotNet.LauncherUI;

public class LogsLogService : ILogEventSink
{
    private readonly List<string> _logs = [];
    public event Action? OnLogReceived;
    public IReadOnlyList<string> Logs => _logs.AsReadOnly();

    private const int MaxLogs = 500;
    private const int RemoveCount = 50;

    public void Emit(Serilog.Events.LogEvent logEvent)
    {
        var message = logEvent.RenderMessage();
        if (_logs.Count > MaxLogs)
        {
            _logs.RemoveRange(0, RemoveCount);
        }

        _logs.Add(message);
        OnLogReceived?.Invoke();
    }
}

public static class LauncherSinkExtensions
{
    public static LoggerConfiguration LogsLogSink(
        this LoggerSinkConfiguration loggerConfiguration,
        LogsLogService service)
        => loggerConfiguration.Sink(service);
}