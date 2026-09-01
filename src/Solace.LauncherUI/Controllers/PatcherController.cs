using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MCEPatcher.Core;
using Serilog;
using Solace.LauncherUI.Patcher;

namespace Solace.LauncherUI.Controllers;

[ApiController]
[Authorize(Policy = Permissions.UsePatcher)]
[Route("api/patcher")]
internal sealed class PatcherController : ControllerBase
{
    private const long MaxUploadBytes = 3L * 1024 * 1024 * 1024; // 3 GB (matches the patcher page)

    private readonly PatcherService _patcherService;

    public PatcherController(PatcherService patcherService)
    {
        _patcherService = patcherService;
    }

    /// <summary>
    /// Streams an original APK/IPA from the browser straight to disk. The patcher page uploads
    /// via XHR instead of the Blazor Server circuit because the circuit's file stream is capped
    /// at ~50 KB per SignalR message (RemoteJSDataStream), which would make a ~66 MB APK need
    /// over a thousand round-trips.
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Upload([FromQuery] PatchPlatform platform, CancellationToken cancellationToken)
    {
        var extension = platform == PatchPlatform.Android ? ".apk" : ".ipa";

        var fileName = Request.Headers.TryGetValue("X-File-Name", out var headerValues)
            ? WebUtility.UrlDecode(headerValues.ToString())
            : $"upload{extension}";

        if (!fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest($"A {extension} file is required for {platform}.");
        }

        var uploadDir = Path.Combine(PatcherService.BaseDir, "uploads");
        Directory.CreateDirectory(uploadDir);

        var storedName = $"{Guid.NewGuid():N}{extension}";
        var targetPath = Path.Combine(uploadDir, storedName);

        long totalBytes;
        string hash;

        try
        {
            await using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var hasher = SHA256.Create();
            var buffer = new byte[81920];
            long total = 0;

            int bytesRead;
            while ((bytesRead = await Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += bytesRead;

                if (total > MaxUploadBytes)
                {
                    throw new InvalidOperationException("The file exceeds the 3 GB upload limit.");
                }

                hasher.TransformBlock(buffer, 0, bytesRead, null, 0);
                await fs.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }

            hasher.TransformFinalBlock([], 0, 0);
            totalBytes = total;
            hash = Convert.ToHexString(hasher.Hash!);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            // the client aborted mid-body (or the file was too large) - drop the partial file
            TryDelete(targetPath);

            return BadRequest(ex is OperationCanceledException ? "Upload cancelled." : ex.Message);
        }

        var verified = platform == PatchPlatform.Android
            ? hash == ApkProcessor.ApkHash
            : hash == IpaProcessor.IpaHash;

        return Ok(new UploadResult(storedName, fileName, totalBytes, hash, verified));
    }

    private static void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Appends one chunk (max ~16 MB) of an APK/IPA upload to the partial file
    /// uploads/{uploadId}.part. The client (App.razor) sends chunks sequentially,
    /// always passing the last offset confirmed by the server, which makes the
    /// upload resumable: a stale retry just gets told the real offset, and a
    /// dropped connection can be retried without re-sending the whole file.
    /// </summary>
    [HttpPost("upload-chunk")]
    [RequestSizeLimit(17L * 1024 * 1024)]
    public async Task<IActionResult> UploadChunk(
        [FromQuery] PatchPlatform platform,
        [FromQuery] string uploadId,
        [FromQuery] long offset,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(uploadId, "N", out var id))
        {
            return BadRequest("Invalid upload id.");
        }

        var extension = platform == PatchPlatform.Android ? ".apk" : ".ipa";
        var uploadDir = Path.Combine(PatcherService.BaseDir, "uploads");
        Directory.CreateDirectory(uploadDir);

        var partPath = Path.Combine(uploadDir, $"{id:N}{extension}.part");
        long current = 0;

        try
        {
            await using var fs = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            current = fs.Length;

            // stale retry (the client is behind the server, e.g. after a dropped
            // response): drain the body so the connection stays clean, then tell
            // the client where the file really ends so it can catch up
            if (offset != current)
            {
                var sink = new byte[81920];

                while (await Request.Body.ReadAsync(sink, cancellationToken) > 0)
                {
                }

                return Ok(new ChunkResult(current));
            }

            // a fresh FileStream starts at position 0 - without this the chunk
            // would overwrite the beginning of the file instead of appending
            _ = fs.Seek(0, SeekOrigin.End);

            var buffer = new byte[81920];
            long written = current;

            int read;
            while ((read = await Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                written += read;

                if (written > MaxUploadBytes)
                {
                    return BadRequest("The file exceeds the 3 GB upload limit.");
                }

                await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            return Ok(new ChunkResult(written));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // partial chunk - the client retries from the last confirmed offset
            return Ok(new ChunkResult(current));
        }
    }

    /// <summary>
    /// Turns a finished chunked upload into a regular upload: moves the .part file
    /// to its final name, hashes it and compares against the original APK/IPA hash.
    /// </summary>
    [HttpPost("upload-finalize")]
    public async Task<IActionResult> UploadFinalize(
        [FromQuery] PatchPlatform platform,
        [FromQuery] string uploadId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(uploadId, "N", out var id))
        {
            return BadRequest("Invalid upload id.");
        }

        var extension = platform == PatchPlatform.Android ? ".apk" : ".ipa";
        var uploadDir = Path.Combine(PatcherService.BaseDir, "uploads");
        var partPath = Path.Combine(uploadDir, $"{id:N}{extension}.part");

        if (!System.IO.File.Exists(partPath))
        {
            return BadRequest("Unknown upload - it may have been finalized already.");
        }

        var fileName = Request.Headers.TryGetValue("X-File-Name", out var headerValues)
            ? WebUtility.UrlDecode(headerValues.ToString())
            : $"upload{extension}";

        if (!fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partPath);
            return BadRequest($"A {extension} file is required for {platform}.");
        }

        var storedName = $"{Guid.NewGuid():N}{extension}";
        var targetPath = Path.Combine(uploadDir, storedName);

        System.IO.File.Move(partPath, targetPath);

        long totalBytes;
        string hash;

        try
        {
            await using var fs = System.IO.File.OpenRead(targetPath);
            totalBytes = fs.Length;
            hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return BadRequest("Upload cancelled.");
        }

        var verified = platform == PatchPlatform.Android
            ? hash == ApkProcessor.ApkHash
            : hash == IpaProcessor.IpaHash;

        return Ok(new UploadResult(storedName, fileName, totalBytes, hash, verified));
    }

    internal sealed record ChunkResult(long Offset);

    internal sealed record UploadResult(string Name, string FileName, long Size, string Hash, bool Verified);


    [HttpGet("download/{jobId}")]
    public IActionResult Download(string jobId)
    {
        var job = _patcherService.GetJob(jobId);

        if (job is not { Status: PatchJobStatus.Succeeded } || job.OutputPath is null || !System.IO.File.Exists(job.OutputPath))
        {
            return NotFound();
        }

        var contentType = job.Platform == PatchPlatform.Android
            ? "application/vnd.android.package-archive"
            : "application/octet-stream";

        // The action returns instantly - any perceived "waiting" is the ~200 MB
        // file streaming over the network. Log both the start and the end of the
        // transfer so a delayed download can be pinpointed (server vs. proxy vs.
        // network), and enable range processing so the browser can resume the
        // download instead of restarting on hiccups.
        Log.Information("Patched client download starting: {FileName}, {Size} bytes, job {JobId}",
            job.OutputFileName, job.OutputFileSize, job.Id);

        var stopwatch = Stopwatch.StartNew();

        // X-Accel-Buffering: no tells reverse proxies (nginx & co.) to stop
        // buffering this response - otherwise the proxy may swallow the whole
        // ~200 MB before forwarding the headers, which the browser experiences
        // as "nothing happens for a minute or two, then it suddenly downloads".
        Response.Headers.Append("X-Accel-Buffering", "no");

        Response.OnCompleted(() =>
        {
            Log.Information("Patched client download finished: {FileName}, {Seconds:F1}s, {Size} bytes, job {JobId}",
                job.OutputFileName, stopwatch.Elapsed.TotalSeconds, job.OutputFileSize, job.Id);
            return Task.CompletedTask;
        });

        return PhysicalFile(job.OutputPath, contentType, fileDownloadName: job.OutputFileName, enableRangeProcessing: true);
    }
}
