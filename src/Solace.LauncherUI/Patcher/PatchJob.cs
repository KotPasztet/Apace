namespace Solace.LauncherUI.Patcher;

public enum PatchMode
{
    Auto,
    Simple,
    Advanced,
}

public enum PatchPlatform
{
    Android,
    Ios,
}

public enum PatchProtocol
{
    Http = 0,
    Https = 1,
}

public static class PatchProtocolExtensions
{
    public static string ToProtocolString(this PatchProtocol protocol)
        => protocol switch
        {
            PatchProtocol.Http => "http",
            PatchProtocol.Https => "https",
            _ => throw new ArgumentOutOfRangeException(nameof(protocol), (int)protocol, null),
        };
}

public enum PatchJobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// A single patch job: the uploaded input file, live log output and the
/// resulting patched APK/IPA once it finishes.
/// </summary>
public sealed class PatchJob
{
    private readonly object logLock = new();
    private readonly List<string> logLines = [];

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public required PatchMode Mode { get; init; }

    public required PatchPlatform Platform { get; init; }

    public required string WorkDir { get; init; }

    /// <summary>Path of the uploaded, original APK/IPA.</summary>
    public required string InputPath { get; init; }

    public required string InputFileName { get; init; }

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public PatchJobStatus Status { get; private set; } = PatchJobStatus.Queued;

    public string? Error { get; private set; }

    /// <summary>Path of the patched APK/IPA (set on success).</summary>
    public string? OutputPath { get; private set; }

    public string OutputFileName { get; private set; } = string.Empty;

    /// <summary>Size of the patched APK/IPA (set on success).</summary>
    public long OutputFileSize { get; private set; }

    public bool IsFinished => Status is PatchJobStatus.Succeeded or PatchJobStatus.Failed;

    public void AddLog(string line)
    {
        lock (logLock)
        {
            // keep the buffer bounded — the UI only shows the tail anyway
            if (logLines.Count >= 2000)
            {
                logLines.RemoveRange(0, 500);
            }

            logLines.Add(line);
        }
    }

    public void AddLog(Serilog.Events.LogEvent logEvent)
    {
        var level = logEvent.Level switch
        {
            Serilog.Events.LogEventLevel.Fatal or Serilog.Events.LogEventLevel.Error => "ERR",
            Serilog.Events.LogEventLevel.Warning => "WRN",
            Serilog.Events.LogEventLevel.Debug or Serilog.Events.LogEventLevel.Verbose => "DBG",
            _ => "INF",
        };

        var message = $"{logEvent.Timestamp.LocalDateTime:HH:mm:ss} [{level}] {logEvent.RenderMessage()}";

        if (logEvent.Exception is not null)
        {
            message += $"{Environment.NewLine}{logEvent.Exception}";
        }

        AddLog(message);
    }

    public IReadOnlyList<string> GetLogSnapshot()
    {
        lock (logLock)
        {
            return logLines.ToArray();
        }
    }

    internal void MarkRunning()
    {
        StartedAt = DateTimeOffset.UtcNow;
        Status = PatchJobStatus.Running;
    }

    internal void MarkSucceeded(string outputPath)
    {
        OutputPath = outputPath;
        OutputFileName = Path.GetFileName(outputPath);

        try
        {
            OutputFileSize = new FileInfo(outputPath).Length;
        }
        catch
        {
        }

        FinishedAt = DateTimeOffset.UtcNow;
        Status = PatchJobStatus.Succeeded;
    }

    internal void MarkFailed(string error)
    {
        Error = error;
        FinishedAt = DateTimeOffset.UtcNow;
        Status = PatchJobStatus.Failed;
    }
}
