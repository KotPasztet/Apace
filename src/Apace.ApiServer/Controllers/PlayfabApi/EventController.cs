using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Apace.ApiServer.Models.Playfab;
using Apace.Common.Utils;
using Serilog;

namespace Apace.ApiServer.Controllers.PlayfabApi;

[Route("Event")]
[Route("20CA2.playfabapi.com/Event")]
[Route("1712756451.playfabapi.com/Event")]
internal sealed class EventController : ApaceControllerBase
{
    // The client's own error/progress reports ride on this endpoint — the
    // "stuck on Wczytywanie" sessions end with one big telemetry batch whose
    // payload used to be swallowed. Log the (truncated) body so a hanging
    // client's reason becomes visible in the panel's live logs.
    private const int MaxLoggedBodyChars = 1500;

    [HttpPost("WriteTelemetryEvents")]
    public async Task<ContentHttpResult> WriteTelemetryEvents()
    {
        try
        {
            string body = await new StreamReader(Request.Body).ReadToEndAsync(Request.HttpContext.RequestAborted);
            if (!string.IsNullOrWhiteSpace(body))
            {
                Log.Information("Telemetry batch ({Bytes} bytes): {Body}",
                    body.Length,
                    body.Length <= MaxLoggedBodyChars ? body : body[..MaxLoggedBodyChars] + "…");
            }
        }
        catch
        {
            // never fail the endpoint over logging — the response below must go out
        }

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
