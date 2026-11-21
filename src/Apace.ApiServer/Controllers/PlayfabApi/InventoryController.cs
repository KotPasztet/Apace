using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apace.ApiServer.Models.Playfab;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Apace.ApiServer.Controllers.PlayfabApi;

// Economy v2 inventory endpoints. The MCE client calls redeem/GetVirtualCurrencies/
// GetInventoryItems as a burst when it syncs persona state (dressing room / store).
//
// GetInventoryItems additionally carries the CONTENT KEY receipt: the client parses
// the response, extracts the "Receipt" token (JWT-shaped: base64url(header).base64url(payload)),
// validates the payload's iss == "https://playfab.com" and
// aud == "https://playfab.com/B63A0803D3653643/" (the MCE title id hash), and registers
// every Receipt.Entitlements[] entry as a pack decryption key:
//   FriendlyId  -> ContentIdentity of the piece pack (the uuid in contents.json @0x11)
//   ContentKey  -> AES-256-CBC key string (key[0:32] = key, key[0:16] = IV) the client
//                  uses to decrypt that pack's contents.json body
// Without a key for a pack's ContentIdentity the client cannot decrypt contents.json
// and rejects the pack; the encrypted-container Apace pieces below therefore pair with
// the entitlements served here. The key constant mirrors APACE_CONTENT_KEY in
// scripts/build_apace_piece.py - keep them in sync.
[Route("inventory")]
[Route("20CA2.playfabapi.com/inventory")]
internal sealed class InventoryController : ApaceControllerBase
{
    private const string ApaceContentKey = "9f2c7a51e8b04d63a7c15f8e2d94b6a0c3e8517d0fa94b62c8d7e1365a04bf19";

    // (piece pack ContentIdentity = manifest uuid, fixed arbitrary InstanceId) per Apace cape
    private static readonly (string FriendlyId, string InstanceId)[] ApaceEntitlements =
    [
        ("068257f6-e709-4a7d-ae48-af8b9455bbd5", "7c1d2e4a-93b5-4f60-8a21-5c4d6e7f8a01"), // Apace Cape 128
        ("7c00868e-1dd7-488e-a6e3-1aa580f67146", "8d2e3f5b-a4c6-4071-9b32-6d5e7f8a9b02"), // Apace Cape 256
        ("01a0a816-4da1-4540-bd57-bf4cda590b62", "9e3f4a6c-b5d7-4182-ac43-7e6f8a9bac03"), // Apace Cape 512
    ];

    private static string BuildReceiptToken()
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>()
        {
            ["iss"] = "https://playfab.com",
            ["aud"] = "https://playfab.com/B63A0803D3653643/",
            ["Receipt"] = new Dictionary<string, object>()
            {
                ["EntityId"] = "Apace",
                ["Entitlements"] = ApaceEntitlements.Select(e => new Dictionary<string, object>()
                {
                    ["FriendlyId"] = e.FriendlyId,
                    ["ContentKey"] = ApaceContentKey,
                    ["CreatorId"] = "Minecraft",
                    ["Quantity"] = 1,
                    ["InstanceId"] = e.InstanceId,
                }).ToArray(),
            },
        });
        // JWT shape header.payload.signature; alg=none, empty signature
        return $"{Base64Url("""{"alg":"none","typ":"JWT"}""")}.{Base64Url(payload)}.";
    }

    private static string Base64Url(string s)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private async Task<string> ReadBodyAsync()
    {
        try
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Log.Information("inventory{Path} request body: {Body}", Request.Path.Value ?? "", body);
            return body;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "failed to read inventory request body");
            return "";
        }
    }

    [HttpPost("GetVirtualCurrencies")]
    public async Task<ContentHttpResult> GetVirtualCurrencies()
    {
        await ReadBodyAsync();
        return JsonPascalCase(new PlayfabOkResponse(
            200,
            "OK",
            new Dictionary<string, object>()
            {
                ["Currencies"] = (IEnumerable<object>)[new Dictionary<string, object>() {
                    ["CurrencyId"] = "ecd19d3c-7635-402c-a185-eb11cb6c6946",
                    ["Amount"] = 0,
                    ["ChangedAmount"] = 0,
                }],
                ["Items"] = Array.Empty<object>(),
            }
        ));
    }

    [HttpPost("redeem")]
    public async Task<ContentHttpResult> Redeem()
    {
        await ReadBodyAsync();
        return JsonPascalCase(new PlayfabOkResponse(
            200,
            "OK",
            new Dictionary<string, object>()
            {
                ["Succeeded"] = Array.Empty<object>(),
                ["Failed"] = Array.Empty<object>(),
            }
        ));
    }

    [HttpPost("GetInventoryItems")]
    public async Task<ContentHttpResult> GetInventoryItems()
    {
        await ReadBodyAsync();
        var receipt = BuildReceiptToken();
        Log.Information("inventory GetInventoryItems serving {Count} Apace content-key entitlements", ApaceEntitlements.Length);
        return JsonPascalCase(new PlayfabOkResponse(
            200,
            "OK",
            new Dictionary<string, object>()
            {
                ["Items"] = Array.Empty<object>(),
                ["ETag"] = "1/MQ==",
                ["ItemMetadata"] = Array.Empty<object>(),
                ["Subscriptions"] = Array.Empty<object>(),
                ["Receipt"] = receipt,
            }
        ));
    }
}
