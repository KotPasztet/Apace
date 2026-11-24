using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Apace.ApiServer.Models.Playfab;
using Apace.ApiServer.Services;
using Apace.Common.Utils;
using Serilog;
using System.Text.Json;

namespace Apace.ApiServer.Controllers.PlayfabApi;

[Route("Event")]
[Route("20CA2.playfabapi.com/Event")]
[Route("1712756451.playfabapi.com/Event")]
internal sealed class EventController : ApaceControllerBase
{
    [HttpPost("WriteTelemetryEvents")]
    public async Task<ContentHttpResult> WriteTelemetryEvents()
    {
        try
        {
            string body = await new StreamReader(Request.Body).ReadToEndAsync(Request.HttpContext.RequestAborted);
            if (!string.IsNullOrWhiteSpace(body))
            {
                LogTelemetryBatch(body);
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

    // The client's own error/progress reports ride on this endpoint — a "stuck
    // on Wczytywanie" session ends with one big telemetry batch carrying the
    // client's view of the failure. A whole-body truncation once cut exactly
    // the interesting events out of a 37 KB batch, so digest instead: known
    // lifecycle events (heartbeats, app pause/resume, sign-in, client start)
    // are summarized on one line; EVERY other event is logged in full, one log
    // line per event, effectively uncapped — the failure report can no longer
    // be lost to a truncation in the middle of the array.
    private static readonly HashSet<string> KnownLifecycleEvents = new(StringComparer.Ordinal)
    {
        "Heartbeat", "AppPaused", "AppResumed", "AppSuspended", "StartClient", "SignInAction",
    };

    private const int MaxEventChars = 30000;

    private static void LogTelemetryBatch(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("Events", out JsonElement events) || events.ValueKind != JsonValueKind.Array)
            {
                Log.Information("Telemetry batch ({Bytes} bytes) has no Events array: {Body}", body.Length, body);
                return;
            }

            var lifecycle = new List<string>();
            int unknown = 0;
            foreach (JsonElement ev in events.EnumerateArray())
            {
                string name = "?";
                if ((ev.TryGetProperty("Name", out JsonElement n) || ev.TryGetProperty("name", out n)) && n.ValueKind == JsonValueKind.String)
                {
                    name = n.GetString() ?? "?";
                }

                if (KnownLifecycleEvents.Contains(name))
                {
                    lifecycle.Add(name);
                    continue;
                }

                RecordMissingPersonaPiece(ev, name);

                unknown++;
                string raw = ev.GetRawText();
                Log.Information("Telemetry event \"{Name}\" ({Bytes} bytes): {Event}",
                    name, raw.Length, raw.Length <= MaxEventChars ? raw : raw[..MaxEventChars] + "…");
            }

            Log.Information("Telemetry batch ({Bytes} bytes): {Total} event(s); lifecycle: {Lifecycle}; NOT-lifecycle events logged above individually: {Unknown}",
                body.Length,
                lifecycle.Count + unknown,
                lifecycle.Count == 0 ? "none" : string.Join(", ", lifecycle.GroupBy(x => x).Select(g => $"{g.Key} x{g.Count()}")),
                unknown);
        }
        catch (JsonException)
        {
            // not the shape we expect — keep the raw payload, generously capped
            Log.Information("Telemetry batch, unparsable ({Bytes} bytes): {Body}", body.Length,
                body.Length <= 60000 ? body : body[..60000] + "…");
        }

        PersonaMissingPieces.Flush(Log.Logger);
    }

    // PersonaCreationFailed "PieceNotInRepository" events carry the piece uuid
    // the client's persona build could not resolve anywhere — the "even flat
    // things don't equip" root cause (the whole persona fails on that one
    // reference). Register it so the profile-serving path drops the reference
    // (PersonaMissingPieces); the failure contract of this endpoint is never
    // to break over telemetry parsing, so every miss is silently ignored.
    private static void RecordMissingPersonaPiece(JsonElement ev, string name)
    {
        if (!string.Equals(name, "PersonaCreationFailed", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (!TryGetEventProperty(ev, "Payload", out JsonElement payload))
            {
                return;
            }

            // some PlayFab shapes wrap the payload as a nested JSON string
            if (payload.ValueKind == JsonValueKind.String)
            {
                using JsonDocument parsed = JsonDocument.Parse(payload.GetString() ?? "{}");
                HandlePayload(parsed.RootElement);
                return;
            }

            if (payload.ValueKind == JsonValueKind.Object)
            {
                HandlePayload(payload);
            }
        }
        catch (JsonException)
        {
            // not JSON — ignore
        }
        catch (Exception)
        {
            // never break the telemetry endpoint over a malformed event
        }

        void HandlePayload(JsonElement payloadObject)
        {
            if (!TryGetEventProperty(payloadObject, "Properties", out JsonElement properties)
                || properties.ValueKind != JsonValueKind.Object
                || !TryGetEventProperty(properties, "Reason", out JsonElement reason)
                || reason.ValueKind != JsonValueKind.String
                || !string.Equals(reason.GetString(), "PieceNotInRepository", StringComparison.Ordinal)
                || !TryGetEventProperty(properties, "PieceId", out JsonElement pieceId)
                || pieceId.ValueKind != JsonValueKind.String
                || pieceId.GetString() is not { Length: > 0 } pieceIdValue)
            {
                return;
            }

            PersonaMissingPieces.Record(pieceIdValue, Log.Logger);
        }
    }

    // Event property names vary in casing between client builds (PascalCase in
    // the observed batches) — case-insensitive lookup on the object.
    private static bool TryGetEventProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }
}
