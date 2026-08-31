using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Solace.LauncherUI.Patcher;

namespace Solace.LauncherUI.Controllers;

[ApiController]
[Authorize(Policy = Permissions.UsePatcher)]
[Route("api/patcher")]
internal sealed class PatcherController : ControllerBase
{
    private readonly PatcherService _patcherService;

    public PatcherController(PatcherService patcherService)
    {
        _patcherService = patcherService;
    }

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
