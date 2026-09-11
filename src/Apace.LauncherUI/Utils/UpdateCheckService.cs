using System.Reflection;
using System.Text.Json;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Apace.LauncherUI.Utils;

public enum UpdateCheckState
{
    Unknown,
    UpToDate,
    UpdateAvailable,
    CheckFailed,
}

/// <summary>
/// Compares the running build against the head of its release channel on GitHub
/// and exposes the result to the panel UI (topbar chip, About page).
///
/// Follows the codebase's singleton + own-timer convention (no IHostedService):
/// registered as a singleton in Program.cs, the first check is scheduled from
/// the ApplicationStarted hook (~30 s after startup), then repeats every 6 h.
/// <see cref="CheckNowAsync"/> allows an on-demand check (About page).
///
/// The repo is public, so the unauthenticated GitHub API is used (no token);
/// its 60 req/h per-IP rate limit is ample for a 6 h interval.
/// </summary>
public sealed class UpdateCheckService : IDisposable
{
    public event Action? OnStateChanged;

    private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private const string ApiBase = "https://api.github.com/repos/KotPasztet/Apace";
    private const string CommitEnvVar = "APACE_COMMIT";
    private const string ChannelEnvVar = "APACE_CHANNEL";
    private const int MinShaLength = 7;
    private const int FullShaLength = 40;

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly ILogger logger = Log.Logger;

    private Timer? _timer;
    private bool _started;
    private int _checkInProgress;
    private UpdateCheckState _state = UpdateCheckState.Unknown;
    private string? _lastError;
    private UpdateCheckState _loggedState = UpdateCheckState.Unknown;
    private string? _loggedLatestSha;

    public UpdateCheckService()
    {
        var assemblySha = ExtractAssemblyCommitSha();
        var envSha = NormalizeSha(Environment.GetEnvironmentVariable(CommitEnvVar));

        // The .NET SDK bakes the git commit into the assembly's informational
        // version ("1.0.0+<sha>"), which survives Docker builds made from a git
        // checkout. When it is missing or truncated, fall back to the
        // APACE_COMMIT env var injected at image build time.
        CurrentSha = assemblySha is { Length: >= FullShaLength } ? assemblySha : (envSha ?? assemblySha);

        Channel = Environment.GetEnvironmentVariable(ChannelEnvVar)?.Trim().ToLowerInvariant() switch
        {
            "dev" => "dev",
            "main" => "main",
            "release" => "release",
            // Unset (bare-metal installs) or unrecognized — assume release zips.
            _ => "release",
        };
    }

    /// <summary>Commit the running build was produced from, or null when unknown (e.g. a plain local build).</summary>
    public string? CurrentSha { get; }

    /// <summary>Release channel: "dev", "main" (both docker) or "release" (bare-metal zips).</summary>
    public string Channel { get; }

    /// <summary>Head commit of the channel as of the last successful check.</summary>
    public string? LatestSha { get; private set; }

    public DateTime? LastChecked { get; private set; }

    public UpdateCheckState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            OnStateChanged?.Invoke();
        }
    }

    public bool IsUpToDate => State == UpdateCheckState.UpToDate;

    /// <summary>8-char form for UI and logs; "(unknown)" when no SHA is available.</summary>
    public static string ShortSha(string? sha)
        => string.IsNullOrEmpty(sha) ? "(unknown)" : sha.Length <= 8 ? sha : sha[..8];

    /// <summary>
    /// Starts the periodic check. Called once from the ApplicationStarted hook,
    /// so the first request doesn't wait on GitHub.
    /// </summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _timer = new Timer(async _ => await CheckNowAsync(), null, FirstCheckDelay, CheckInterval);
    }

    /// <summary>
    /// Runs a check immediately (on demand or from the timer). Never throws;
    /// a single check runs at a time (timer and on-demand calls share the guard).
    /// </summary>
    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _lastError = null;
            var latestSha = await FetchLatestShaAsync(cancellationToken);
            LastChecked = DateTime.UtcNow;

            if (latestSha is null)
            {
                _lastError = _lastError ?? $"could not determine the latest version of the '{Channel}' channel";
                State = UpdateCheckState.CheckFailed;
            }
            else
            {
                LatestSha = latestSha;
                if (CurrentSha is null)
                {
                    _lastError = "this build has no commit information to compare against";
                    State = UpdateCheckState.CheckFailed;
                }
                else
                {
                    State = string.Equals(CurrentSha, latestSha, StringComparison.OrdinalIgnoreCase)
                        ? UpdateCheckState.UpToDate
                        : UpdateCheckState.UpdateAvailable;
                }
            }

            LogResultOnce();
        }
        catch (OperationCanceledException)
        {
            // Shutdown / aborted request — leave the previous state alone.
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            LastChecked = DateTime.UtcNow;
            State = UpdateCheckState.CheckFailed;
            LogResultOnce();
        }
        finally
        {
            Interlocked.Exchange(ref _checkInProgress, 0);
        }
    }

    private async Task<string?> FetchLatestShaAsync(CancellationToken cancellationToken)
    {
        return Channel switch
        {
            "dev" or "main" => await GetCommitShaAsync(Channel, cancellationToken),
            // Latest release tag, resolved to the commit it points at.
            "release" => await GetLatestReleaseShaAsync(cancellationToken),
            _ => null,
        };
    }

    /// <summary>GET /commits/{branch-or-tag} → head commit of a branch or tag.</summary>
    private async Task<string?> GetCommitShaAsync(string reference, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"{ApiBase}/commits/{Uri.EscapeDataString(reference)}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.Debug("Update check: HTTP {StatusCode} from /commits/{Reference}", (int)response.StatusCode, reference);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return NormalizeSha(document.RootElement.GetProperty("sha").GetString());
    }

    /// <summary>GET /releases/latest → tag_name, then the commit that tag points at.</summary>
    private async Task<string?> GetLatestReleaseShaAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"{ApiBase}/releases/latest", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.Debug("Update check: HTTP {StatusCode} from /releases/latest", (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var tag = document.RootElement.GetProperty("tag_name").GetString();

        if (string.IsNullOrEmpty(tag))
        {
            return null;
        }

        return await GetCommitShaAsync(tag, cancellationToken);
    }

    /// <summary>Logs the outcome once per change of result, not on every 6 h repeat.</summary>
    private void LogResultOnce()
    {
        if (_loggedState == State && _loggedLatestSha == LatestSha)
        {
            return;
        }

        _loggedState = State;
        _loggedLatestSha = LatestSha;

        switch (State)
        {
            case UpdateCheckState.UpToDate:
                logger.Information("Apace {Channel} is up to date ({Sha})", Channel, ShortSha(LatestSha));
                break;
            case UpdateCheckState.UpdateAvailable:
                logger.Information("Update available: {CurrentSha} → {LatestSha} (channel {Channel})", ShortSha(CurrentSha), ShortSha(LatestSha), Channel);
                break;
            case UpdateCheckState.CheckFailed:
                logger.Warning("Update check failed: {Reason}", _lastError);
                break;
        }
    }

    private static string? ExtractAssemblyCommitSha()
    {
        var informationalVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informationalVersion is null)
        {
            return null;
        }

        // "1.0.0+<sha>" — the SDK appends the git commit (SourceRevisionId).
        var separator = informationalVersion.IndexOf('+');
        return separator >= 0 ? NormalizeSha(informationalVersion[(separator + 1)..]) : null;
    }

    private static string? NormalizeSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.Length < MinShaLength || value.Length > FullShaLength)
        {
            return null;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return null;
            }
        }

        return value.ToLowerInvariant();
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
