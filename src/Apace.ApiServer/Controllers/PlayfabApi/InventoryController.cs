using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apace.ApiServer.Models.Playfab;
using Apace.ApiServer.Services;
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
//   ContentKey  -> key string (key[0:32] = AES key, key[0:16] = IV) the client derives
//                  for that pack's contents.json body
// The client, however, decrypts PERSONA packs with its own BUILT-IN key (below) —
// "used to decrypt/encrypt every Skin Pack and Persona on the Minecraft Marketplace...
// the same key is used every single time" (AES-256-CFB8, silica.codes key disclosure,
// MerelyMiserable/MerelyCapes ships the same pair against the live persona pipeline).
// The entitlements below therefore mirror that built-in key per ContentIdentity (the
// derivation matches), so key providers that consult receipts resolve the same bytes;
// they must never deliver a DIFFERENT key for a persona pack uuid or decryption breaks.
// The key constant mirrors SKIN_PERSONA_KEY in scripts/build_apace_piece.py — keep in sync.
[Route("inventory")]
[Route("20CA2.playfabapi.com/inventory")]
[Route("1712756451.playfabapi.com/inventory")]
internal sealed class InventoryController : ApaceControllerBase
{
    private const string ApaceContentKey = "s5s5ejuDru4uchuF2drUFuthaspAbepE";

    // (piece pack ContentIdentity = the piece uuid, fixed arbitrary InstanceId) per Apace cape.
    // The pack ContentIdentity IS the piece uuid now (the rebuilt containers use one uuid:
    // manifest.header.uuid == contents.json header uuid == meta piece_id == catalog PieceId —
    // the old separate pack uuids never matched the real-piece shape and the cape never applied).
    private static readonly (string FriendlyId, string InstanceId)[] ApaceEntitlements =
    [
        ("3199377b-130a-4189-9b90-484ae751c54f", "7c1d2e4a-93b5-4f60-8a21-5c4d6e7f8a01"), // Apace Cape 128
        ("a76970a2-33fe-48f6-b8fc-dd54679f5cdb", "8d2e3f5b-a4c6-4071-9b32-6d5e7f8a9b02"), // Apace Cape 256
        ("5cae9ae7-bf1b-4ee2-8edd-9615743ea02c", "9e3f4a6c-b5d7-4182-ac43-7e6f8a9bac03"), // Apace Cape 512
    ];

    private static string BuildReceiptToken(out int entitlementCount, out int customCount)
    {
        // custom cosmetics first, then the built-in Apace capes — the merge dedupes
        // by FriendlyId (pack uuid) with the panel's custom entries taking
        // precedence over a same-named built-in pack
        var entitlements = new Dictionary<string, (string ContentKey, string InstanceId)>(StringComparer.OrdinalIgnoreCase);
        foreach (var custom in CustomCosmetics.Entitlements)
        {
            entitlements[custom.PackUuid] = (custom.ContentKey, StableInstanceId(custom.PackUuid));
        }

        customCount = entitlements.Count;
        foreach (var e in ApaceEntitlements)
        {
            if (!entitlements.ContainsKey(e.FriendlyId))
            {
                entitlements[e.FriendlyId] = (ApaceContentKey, e.InstanceId);
            }
        }

        if (customCount > 0)
        {
            Log.Debug("inventory GetInventoryItems adding {Count} custom cosmetic entitlement(s) from custom-cosmetics.json", customCount);
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, object>()
        {
            ["iss"] = "https://playfab.com",
            ["aud"] = "https://playfab.com/B63A0803D3653643/",
            ["Receipt"] = new Dictionary<string, object>()
            {
                ["EntityId"] = "Apace",
                ["Entitlements"] = entitlements.Select(e => new Dictionary<string, object>()
                {
                    ["FriendlyId"] = e.Key,
                    ["ContentKey"] = e.Value.ContentKey,
                    ["CreatorId"] = "Minecraft",
                    ["Quantity"] = 1,
                    ["InstanceId"] = e.Value.InstanceId,
                }).ToArray(),
            },
        });
        // JWT shape header.payload.signature; alg=none, empty signature
        entitlementCount = entitlements.Count;
        return $"{Base64Url("""{"alg":"none","typ":"JWT"}""")}.{Base64Url(payload)}.";
    }

    // Custom cosmetics need a receipt InstanceId (same field shape as the built-in
    // capes) whose only requirement is stability across requests — derive it
    // deterministically from the pack uuid instead of storing one per item.
    private static string StableInstanceId(string packUuid)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("apace-custom-entitlement:" + packUuid));
        return new Guid(hash.AsSpan(0, 16)).ToString();
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
        var receipt = BuildReceiptToken(out int entitlementCount, out int customCount);
        Log.Information("inventory GetInventoryItems serving {Count} content-key entitlements ({Custom} custom cosmetic(s))", entitlementCount, customCount);
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
