using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MCEPatcher.Core;
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

        return PhysicalFile(job.OutputPath, contentType, fileDownloadName: job.OutputFileName);
    }
}
