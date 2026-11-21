using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Apace.ApiServer.Models.Playfab;
using Serilog;

namespace Apace.ApiServer.Controllers.PlayfabApi;

// Economy v2 inventory endpoints. The MCE client calls redeem/GetVirtualCurrencies/
// GetInventoryItems as a burst when it syncs persona state (dressing room / store),
// and later purchase-style calls when claiming items. Bodies are logged at
// Information level: the dress/claim wire format is still being mapped and these
// logs are how the next missing endpoint gets identified.
[Route("inventory")]
[Route("20CA2.playfabapi.com/inventory")]
internal sealed class InventoryController : ApaceControllerBase
{
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
        return JsonPascalCase(new PlayfabOkResponse(
            200,
            "OK",
            new Dictionary<string, object>()
            {
                ["Items"] = Array.Empty<object>(),
                ["ETag"] = "1/MQ==",
                ["ItemMetadata"] = Array.Empty<object>(),
                ["Subscriptions"] = Array.Empty<object>(),
            }
        ));
    }
}
