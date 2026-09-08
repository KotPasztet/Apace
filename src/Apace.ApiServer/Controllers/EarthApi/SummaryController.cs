using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Apace.ApiServer.Controllers.EarthApi;

[AllowAnonymous]
[ApiVersion("1.1")]
[Route("1")]
internal sealed class SummaryController : ApaceControllerBase
{
    [HttpGet("summary")]
    public ContentHttpResult Get()
        => EarthJson(new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["updates"] = new Dictionary<string, object>()
        });
}
