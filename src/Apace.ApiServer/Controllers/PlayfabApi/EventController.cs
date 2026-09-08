using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Apace.ApiServer.Models.Playfab;
using Apace.Common.Utils;

namespace Apace.ApiServer.Controllers.PlayfabApi;

[Route("Event")]
[Route("20CA2.playfabapi.com/Event")]
internal sealed class EventController : ApaceControllerBase
{
    [HttpPost("WriteTelemetryEvents")]
    public ContentHttpResult WriteTelemetryEvents()
    {
        return JsonPascalCase(new PlayfabOkResponse(
            200,
            "OK",
            new Dictionary<string, object>()
            {
                ["AssignedEventIds"] = Array.Empty<string>(),
            }
        ));
    }
}
