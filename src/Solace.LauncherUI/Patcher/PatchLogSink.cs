using Serilog.Core;
using Serilog.Events;

namespace Solace.LauncherUI.Patcher;

/// <summary>
/// Serilog sink that routes log events emitted by <c>MCEPatcher.Core</c>
/// (which logs through the static <c>Serilog.Log</c>) into the currently
/// running patch job, so the Client Patcher page can show live output.
/// </summary>
/// <remarks>
/// The current job is tracked via <see cref="AsyncLocal{T}"/> so the value
/// flows through the whole async call chain of <c>ApkProcessor.Run</c> /
/// <c>IpaProcessor.Run</c> without affecting anything else running in the app.
/// </remarks>
public sealed class PatchLogSink : ILogEventSink
{
    public static readonly PatchLogSink Instance = new PatchLogSink();

    private static readonly AsyncLocal<PatchJob?> currentJob = new();

    public static PatchJob? CurrentJob
    {
        get => currentJob.Value;
        set => currentJob.Value = value;
    }

    private PatchLogSink()
    {
    }

    public void Emit(Serilog.Events.LogEvent logEvent)
    {
        currentJob.Value?.AddLog(logEvent);
    }
}
