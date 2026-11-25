using Serilog;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using ILogger = Serilog.ILogger;

namespace Apace.LauncherUI;

/// <summary>
/// Outcome of a resource pack download session: <see cref="Success"/> is true
/// only when a fully downloaded, validated pack is in place.
/// </summary>
public sealed record ResourcePackDownloadResult(bool Success, string? Error = null);

// Downloads staticdata/resourcepacks/vanilla.zip on demand (panel "Server Status" prompt).
//
// The pack (~131 MB) has no official home anymore: Minecraft Earth's
// cdn.mceserv.net is gone and the Wayback Machine is the only real source,
// which comes with quirks this downloader is built around:
//
//  - web.archive.org rate limits aggressively (HTTP 429) and intermittently
//    answers 5xx, so every mirror is retried with backoff (honouring Retry-After).
//  - The "/web/2/" shortcut redirects to the *latest* capture of the URL,
//    which is not necessarily a 200 capture of the pack itself. The CDX API
//    is used to list the actual 200 captures, and each is tried as an
//    explicit timestamped "id_" URL (raw bytes, no replay rewriting).
//  - A slow-but-healthy transfer must not be killed by a blanket timeout:
//    a plain HttpClient dies to its default 100 s timeout in the middle of
//    the download. Instead, each attempt gets a generous overall budget plus
//    a stall watchdog (no bytes for 90 s cancels the attempt), and the whole
//    session has a hard budget.
//  - Partial data is kept in a ".part" file between attempts and resumed
//    with Range requests when the server honours them (a 206 response is
//    verified to continue exactly where the file ends; anything else
//    restarts from zero).
//
// The download never throws: failures are reported through
// <see cref="ResourcePackDownloadResult"/> with the reason shown in the
// panel, and the destination file is only replaced by a fully downloaded,
// validated pack (a ZIP containing a "genoa.mcpack" entry). Only one
// download can run at a time; concurrent DownloadAsync callers get a
// friendly result instead of queueing behind it (the panel button wants
// immediate feedback), while WaitForCurrentDownloadAsync lets the start
// flow WAIT for an already-running session instead of starting a second
// one for the same file. A download started from a browser tab keeps
// running server side even if the tab is closed mid-way.
internal static class ResourcePackDownloader
{
    // The original Minecraft Earth CDN location of the vanilla resource pack.
    private const string OriginalPackUrl = "https://cdn.mceserv.net/availableresourcepack/resourcepacks/dba38e59-091a-4826-b76a-a08d7de5a9e2-1301b0c257a311678123b9e7325d0d6c61db3c35";

    public const long MinResourcePackSize = 100_000_000;       // hard floor for "a plausible pack"
    public const long ExpectedResourcePackSize = 131_885_348;  // exact size of the known good pack
    private const long MaxResourcePackSize = 150_000_000;      // anything bigger is a runaway or a mirror bug

    private static readonly TimeSpan SessionBudget = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan AttemptBudget = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(120);
    private const int MaxAttemptsPerMirror = 2;

    private static readonly SemaphoreSlim _downloadLock = new(1, 1);
    private static CancellationTokenSource? _cancelSource;

    public static bool IsDownloading => _downloadLock.CurrentCount == 0;

    // ---- In-flight session handle ----
    //
    // The task of the newest download session is published here while the
    // semaphore is held, so callers that did not start the download (the
    // server start flow, another browser tab) can await its completion via
    // WaitForCurrentDownloadAsync() instead of launching a second download
    // of the same file.
    private static Task<ResourcePackDownloadResult> _lastSession =
        Task.FromResult(new ResourcePackDownloadResult(false, "No download has been started."));

    // Latest progress (0..1) of the current/last session, so UIs that did not
    // start the download (the start flow) can still animate the progress bar.
    private static float _sessionProgress;

    /// <summary>Progress (0..1) of the current or most recent download session.</summary>
    public static float CurrentProgress => Volatile.Read(ref _sessionProgress);

    /// <summary>
    /// Waits for the download session that is currently in flight (if any) to
    /// finish. Returns immediately when nothing is downloading.
    /// </summary>
    public static async Task WaitForCurrentDownloadAsync()
    {
        while (IsDownloading)
        {
            Task<ResourcePackDownloadResult> current = Volatile.Read(ref _lastSession);
            if (!current.IsCompleted)
            {
                await current;
            }
            else
            {
                // The semaphore is held but the published session already
                // completed: a new session is inside its microscopic setup
                // window (lock acquired, task not published yet). Give it a
                // moment and re-check instead of hot-spinning.
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }
        }
    }

    public static void Cancel()
    {
        try { _cancelSource?.Cancel(); }
        catch (ObjectDisposedException) { /* download finished in the meantime */ }
    }

    private static readonly HttpClient _httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            // All timeouts are per attempt (attempt CTSs) and per session; the
            // client itself must never kill a healthy slow download.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _ = client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"KotPasztet/Apace/{Assembly.GetExecutingAssembly().GetName().Version}");
        return client;
    }

    public static async Task<ResourcePackDownloadResult> DownloadAsync(string destPath, ILogger logger, IProgress<float>? progress = null)
    {
        if (!await _downloadLock.WaitAsync(TimeSpan.Zero))
        {
            return new ResourcePackDownloadResult(false, "A resource pack download is already in progress — it may have been started from another browser tab.");
        }

        // We hold the lock: run the session and publish its task so that
        // WaitForCurrentDownloadAsync can await it (start flow, other tabs).
        Volatile.Write(ref _sessionProgress, 0f);
        var cancelSource = new CancellationTokenSource();
        _cancelSource = cancelSource;
        Task<ResourcePackDownloadResult> session = RunSessionAsync(destPath, logger, progress, cancelSource);
        Volatile.Write(ref _lastSession, session);
        return await session;
    }

    private static async Task<ResourcePackDownloadResult> RunSessionAsync(
        string destPath, ILogger logger, IProgress<float>? progress, CancellationTokenSource cancelSource)
    {
        try
        {
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancelSource.Token);
            sessionCts.CancelAfter(SessionBudget);
            return await DownloadCoreAsync(destPath, logger, progress, sessionCts);
        }
        catch (OperationCanceledException) when (cancelSource.IsCancellationRequested)
        {
            return new ResourcePackDownloadResult(false, "Download cancelled.");
        }
        catch (OperationCanceledException)
        {
            return new ResourcePackDownloadResult(false, $"Download timed out after {SessionBudget.TotalMinutes:F0} minutes — the progress made is kept, trying again resumes where it stopped.");
        }
        catch (Exception ex)
        {
            return new ResourcePackDownloadResult(false, $"Unexpected error: {ex.Message}");
        }
        finally
        {
            _cancelSource = null;
            cancelSource.Dispose();
            _downloadLock.Release();
        }
    }

    private static async Task<ResourcePackDownloadResult> DownloadCoreAsync(string destPath, ILogger logger, IProgress<float>? progress, CancellationTokenSource sessionCts)
    {
        var sessionToken = sessionCts.Token;
        string partPath = destPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        var mirrors = await BuildMirrorUrlsAsync(logger, sessionToken);
        var failures = new List<string>();

        foreach (var url in mirrors)
        {
            for (int attempt = 1; attempt <= MaxAttemptsPerMirror && !sessionToken.IsCancellationRequested; attempt++)
            {
                var failure = await TryDownloadOnceAsync(url, partPath, progress, logger, sessionToken);
                if (failure is null)
                {
                    try
                    {
                        ValidatePackFile(partPath);
                    }
                    catch (InvalidDataException ex)
                    {
                        // The mirror lied about having the pack: whatever it
                        // sent is not usable, and as a resume base it is worse
                        // than nothing, so drop it and try the next mirror.
                        logger.Warning("Resourcepack from {Url} failed validation: {Reason}", url, ex.Message);
                        TryDelete(partPath);
                        failures.Add($"{Host(url)}: {ex.Message}");
                        break;
                    }

                    File.Move(partPath, destPath, overwrite: true);
                    long size = new FileInfo(destPath).Length;
                    logger.Information("Resourcepack downloaded successfully ({Size} bytes) from {Url}", size, url);
                    ReportProgress(progress, size, size);
                    return new ResourcePackDownloadResult(true);
                }

                logger.Warning("Resourcepack download from {Url} failed (attempt {Attempt}/{MaxAttempts}): {Reason}", url, attempt, MaxAttemptsPerMirror, failure.Reason);
                failures.Add($"{Host(url)}: {failure.Reason}");

                if (attempt < MaxAttemptsPerMirror)
                {
                    TimeSpan delay = failure.RetryAfter ?? TimeSpan.FromSeconds(5 * attempt);
                    if (delay > MaxBackoff)
                    {
                        delay = MaxBackoff;
                    }

                    logger.Information("Retrying {Url} in {Seconds:F0}s", url, delay.TotalSeconds);
                    try { await Task.Delay(delay, sessionToken); }
                    catch (OperationCanceledException) { break; }
                }
            }

            if (sessionToken.IsCancellationRequested)
            {
                break;
            }
        }

        // Cancel button / session budget — translated by DownloadAsync.
        sessionToken.ThrowIfCancellationRequested();

        string reasons = string.Join("; ", failures.Distinct().Take(4));
        logger.Warning("Could not download the resourcepack: {Reasons}", reasons);
        logger.Warning("The Minecraft Earth resourcepack can also be downloaded manually from the Internet Archive and saved as {Path}", destPath);
        return new ResourcePackDownloadResult(false, $"All download sources failed ({reasons}). The pack can also be downloaded manually and placed at '{destPath}'.");
    }

    private static async Task<List<string>> BuildMirrorUrlsAsync(ILogger logger, CancellationToken ct)
    {
        List<string> mirrors = [];

        try
        {
            var snapshots = await QueryWaybackSnapshotsAsync(ct);
            mirrors.AddRange(snapshots);
            logger.Information("Wayback Machine has {Count} good snapshot(s) of the resourcepack", snapshots.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Warning("Could not list Wayback Machine snapshots, falling back to the latest-snapshot URL: {Reason}", ex.Message);
        }

        // Fallbacks: the latest-snapshot shortcut, then the (long dead)
        // original CDN in case it ever comes back.
        mirrors.Add($"https://web.archive.org/web/2/{OriginalPackUrl}");
        mirrors.Add(OriginalPackUrl);

        return [.. mirrors.Distinct()];
    }

    private static async Task<List<string>> QueryWaybackSnapshotsAsync(CancellationToken ct)
    {
        // CDX lists every capture of the pack URL; keep captures where the
        // origin answered 200 and collapse identical captures (same digest).
        string cdxUrl = "https://web.archive.org/cdx/search/cdx"
            + "?url=" + Uri.EscapeDataString(OriginalPackUrl)
            + "&output=json&fl=timestamp,statuscode&filter=statuscode:200&collapse=digest&limit=10";

        List<string> urls = [];
        using var response = await _httpClient.GetAsync(cdxUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return urls;
        }

        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2)
            {
                continue;
            }

            string? timestamp = row[0].GetString();
            string? statusCode = row[1].GetString();
            // The first CDX row is the header ("timestamp", "statuscode"),
            // which the 14-digit check below filters out naturally.
            if (timestamp is { Length: 14 } && timestamp.All(char.IsDigit) && statusCode == "200")
            {
                urls.Add($"https://web.archive.org/web/{timestamp}id_/{OriginalPackUrl}");
            }
        }

        return urls;
    }

    private sealed record AttemptFailure(string Reason, bool Retryable, TimeSpan? RetryAfter = null);

    /// <summary>
    /// One download attempt from one mirror. Returns null on success or the
    /// failure description. Retainable partial data is left in the .part file
    /// so the next attempt (this or another mirror) can resume it.
    /// </summary>
    private static async Task<AttemptFailure?> TryDownloadOnceAsync(string url, string partPath, IProgress<float>? progress, ILogger logger, CancellationToken sessionToken)
    {
        long resumeFrom = 0;
        try
        {
            if (File.Exists(partPath))
            {
                resumeFrom = new FileInfo(partPath).Length;
            }
        }
        catch (IOException)
        {
            // Stat races are not worth aborting over; just download fresh.
        }

        if (resumeFrom is < 1 or > MaxResourcePackSize)
        {
            resumeFrom = 0;
        }

        if (resumeFrom > 0)
        {
            logger.Information("Resuming resourcepack download from {Offset} bytes", resumeFrom);
        }

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        attemptCts.CancelAfter(AttemptBudget);
        // Stall watchdog: cancels the attempt when no bytes arrive for a while.
        using var stallTimer = new Timer(_ => attemptCts.Cancel());
        void FeedWatchdog()
        {
            try { stallTimer.Change(StallTimeout, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* attempt already over */ }
        }

        try
        {
            FeedWatchdog();

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (resumeFrom > 0)
            {
                request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new("HTTP 404 — this mirror does not have the pack", Retryable: false);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new("HTTP 403 — this mirror refused the download", Retryable: false);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new("HTTP 429 — rate limited", Retryable: true, RetryAfter: ParseRetryAfter(response));
            }

            if ((int)response.StatusCode >= 500)
            {
                return new($"HTTP {(int)response.StatusCode} — the mirror is temporarily broken", Retryable: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new($"HTTP {(int)response.StatusCode}", Retryable: false);
            }

            // Did the server honour the resume, and what is the full size?
            long totalSize;
            bool resumed = false;
            ContentRangeHeaderValue? contentRange = response.Content.Headers.ContentRange;
            if (response.StatusCode == HttpStatusCode.PartialContent && contentRange?.From == resumeFrom && resumeFrom > 0)
            {
                resumed = true;
                totalSize = contentRange.Length ?? -1;
            }
            else
            {
                resumeFrom = 0;
                totalSize = response.Content.Headers.ContentLength ?? -1;
            }

            if (totalSize >= 0 && totalSize < MinResourcePackSize)
            {
                return new($"this mirror serves a {totalSize} byte file, which cannot be the pack", Retryable: false);
            }

            if (totalSize > MaxResourcePackSize)
            {
                return new($"this mirror claims the pack is {totalSize} bytes, which is way too big", Retryable: false);
            }

            long progressTotal = totalSize > 0 ? totalSize : ExpectedResourcePackSize;
            ReportProgress(progress, progressTotal, resumeFrom);

            await using var fileStream = new FileStream(
                partPath,
                resumed ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);
            await using var contentStream = await response.Content.ReadAsStreamAsync(attemptCts.Token);

            var buffer = new byte[81920];
            long downloadedBytes = resumeFrom;
            long nextProgressAt = resumeFrom + 1_000_000; // ~131 progress updates for the whole pack
            while (true)
            {
                FeedWatchdog();
                int bytesRead = await contentStream.ReadAsync(buffer, attemptCts.Token);
                if (bytesRead == 0)
                {
                    break;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), attemptCts.Token);
                downloadedBytes += bytesRead;

                if (downloadedBytes > MaxResourcePackSize)
                {
                    return new("the mirror keeps sending data past the end of the pack", Retryable: false);
                }

                if (downloadedBytes >= nextProgressAt)
                {
                    ReportProgress(progress, progressTotal, downloadedBytes);
                    nextProgressAt = downloadedBytes + 1_000_000;
                }
            }

            await fileStream.FlushAsync(attemptCts.Token);

            // A known full size that does not match the received byte count
            // means a truncated body — retry (the .part file keeps the bytes).
            if (totalSize > 0 && downloadedBytes != totalSize)
            {
                return new($"connection dropped at {downloadedBytes} of {totalSize} bytes", Retryable: true);
            }

            return null; // success
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
            throw; // cancel button or session budget — ends the whole session
        }
        catch (OperationCanceledException)
        {
            return new($"no data for {StallTimeout.TotalSeconds:F0}s or attempt exceeded {AttemptBudget.TotalMinutes:F0} min", Retryable: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            return new(ex.Message, Retryable: true);
        }
    }

    private static void ValidatePackFile(string partPath)
    {
        long length = new FileInfo(partPath).Length;
        if (length < MinResourcePackSize)
        {
            throw new InvalidDataException($"the downloaded file is only {length} bytes");
        }

        using var zip = ZipFile.OpenRead(partPath);
        if (zip.GetEntry("genoa.mcpack") is null)
        {
            throw new InvalidDataException("the downloaded file is a ZIP but does not contain genoa.mcpack");
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        TimeSpan? delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay is not { } span)
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Clamp(span.TotalSeconds, 1, MaxBackoff.TotalSeconds));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Host(string url) => new Uri(url).Host;

    private static void ReportProgress(IProgress<float>? progress, long total, long downloaded)
    {
        if (total <= 0)
        {
            return;
        }

        float fraction = Math.Clamp((float)downloaded / total, 0f, 1f);
        Volatile.Write(ref _sessionProgress, fraction);

        if (progress is null)
        {
            return;
        }

        try
        {
            progress.Report(fraction);
        }
        catch
        {
            // A disposed Blazor circuit (closed tab) must not abort the
            // download — it keeps running server side.
        }
    }
}
