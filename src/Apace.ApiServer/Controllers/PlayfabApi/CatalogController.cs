using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using OData2Linq;
using Serilog;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Apace.ApiServer.Models.Playfab;
using Apace.ApiServer.Services;
using Apace.Common.Utils;
using CItem = Apace.StaticData.Playfab.Item;

namespace Apace.ApiServer.Controllers.PlayfabApi;

[Route("Catalog")]
[Route("20CA2.playfabapi.com/Catalog")]
internal sealed class CatalogController : ApaceControllerBase
{
    private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new UtcDateTimeConverter() },
    };

    private static StaticData.StaticData StaticData => Program.staticData;

    private static readonly Item[] itemData;

    static CatalogController()
    {
        var brfPrice = new Item.PriceR([
            new([new("ecd19d3c-7635-402c-a185-eb11cb6c6946", "ecd19d3c-7635-402c-a185-eb11cb6c6946", "ecd19d3c-7635-402c-a185-eb11cb6c6946", 0)]),
            new([new("0113e233-7637-48e7-91b0-349fdc74713d", "0113e233-7637-48e7-91b0-349fdc74713d", "0113e233-7637-48e7-91b0-349fdc74713d", 0)])
        ], []);

        itemData = [
            // required for shop to load for some reason...
            new Item(
                new("B63A0803D3653643", "namespace", "namespace"),
                new("B63A0803D3653643", "namespace", "namespace"),
                Guid.Parse("230f5996-04b2-4f0e-83e5-4056c7f1d946"),
                "bundle",
                [new("FriendlyId", Guid.Parse("53bee6fe-c9d9-43c9-b3af-4c5438fba4b7"))],
                null,
                new() { ["en-US"] = "Bold Rabbit Feet", ["NEUTRAL"] = "Bold Rabbit Feet", ["neutral"] = "Bold Rabbit Feet", },
                new() { ["en-US"] = "§", ["NEUTRAL"] = "§", ["neutral"] = "§", },
                new() { ["en-US"] = new(["Animal"]), ["NEUTRAL"] = new(["Animal"]), ["neutral"] = new(["Animal"]), },
                "PersonaDurable",
                new("301F442C3B63DC20", "master_player_account", "master_player_account"),
                new("301F442C3B63DC20", "master_player_account", "master_player_account"),
                false, // IsStackable
                ["android.amazonappstore", "android.googleplay",  "b.store",  "ios.store",  "nx.store",  "oculus.store.gearvr", "oculus.store.rift", "uwp.store",  "uwp.store.mobile",  "xboxone.store", "title.bedrockvanilla", "title.earth"],
                ["230f5996-04b2-4f0e-83e5-4056c7f1d946", "4f7cdadd-a33c-489d-8969-752ca689f567", "tag.animal", "1P"],
                new(2020, 12, 7, 22, 46, 33, 066, DateTimeKind.Utc),
                new(2023, 8, 10, 14, 11, 19, 81, DateTimeKind.Utc),
                null,
                [new Dictionary<string,object>() {
                    ["Id"] = "f4a2cf48-45c1-4fda-86d0-9d24c069f0a9",
                    ["Url"] = "https://xforgeassets001.xboxlive.com/pf-title-b63a0803d3653643-20ca2/f4a2cf48-45c1-4fda-86d0-9d24c069f0a9/primary.zip",
                    ["MaxClientVersion"] = "65535.65535.65535",
                    ["MinClientVersion"] = "1.13.0",
                    ["Tags"] = Array.Empty<string>(),
                    ["Type"] = "personabinary",
                }],
                [new("e7314d2a-8097-48f0-b0e8-039084a22049", "Thumbnail", "Thumbnail", "/playfab/images/shoes_bold_striped_rabbit_thumbnail_0.png")],
                [new(Guid.Parse("8eb22e2c-db50-4e30-a3d2-0c355e479e74"), 1)],
                brfPrice,
                brfPrice,
                [],
                Item.DisplayPropertiesR.CreatePersona(
                    "Minecraft",
                    0,
                    true,
                    "rare",
                    [new("persona_piece", Guid.Parse("4f7cdadd-a33c-489d-8969-752ca689f567"), "1.1.0"),],
                    Guid.Parse("53bee6fe-c9d9-43c9-b3af-4c5438fba4b7"),
                    "persona_feet"
                )
            ),
            .. StaticData.Playfab.Items.Select(item => CIItemToItem(item.Value)),
        ];

        // Bold Rabbit Feet item id — hardcoded above and required for the shop to
        // load, so it survives the live-catalog offer even though it is a 1P persona item.
        BoldRabbitFeetItemId = itemData[0].Id;
    }

    private static Guid BoldRabbitFeetItemId { get; }

    // The live persona marketplace (panel-toggled): when a synced catalog exists,
    // the offer is the LIVE catalog — every 1P "Minecraft" persona piece from the
    // legacy staticdata items drops out (their binaries are long dead; the live
    // catalog replaces them) and the unlock-all live items are appended. The Apace
    // custom pieces (creator KotPasztet), the client-config entries (layout,
    // StoreConfig, skin packs) and the hardcoded Bold Rabbit Feet stay.
    private static IEnumerable<Item> BaseOfferItems
    {
        get
        {
            if (!LivePersonaCatalog.IsAvailable)
            {
                return itemData;
            }

            return itemData.Where(item =>
                item.ContentType != "PersonaDurable" ||
                item.Id == BoldRabbitFeetItemId ||
                item.DisplayProperties?.CreatorName is not "Minecraft");
        }
    }

    // Mapped live offer items, cached per index version (the dressing room fires
    // many searches; mapping ~10k items per request would dominate the request).
    // One immutable record per index stamp: the items, their gating theme tags
    // (aligned by index) and the id maps the pack gate and GetPublishedItem
    // resolve against.
    // Custom cosmetics (panel-defined, hot-reloaded from the volume) mapped to
    // offer items, cached per store stamp like the live offer per index stamp.
    // Creator is never "Minecraft", so the pack gate exempts them (auto-gating);
    // the onlySupported compat gate still applies via PersonaPieceTypes.
    private static long? _customOfferStamp;
    private static Item[]? _customOfferItems;

    private static Item[] CustomOfferItems
    {
        get
        {
            ulong stamp = CustomCosmetics.Stamp;
            if (_customOfferStamp != (long)stamp || _customOfferItems is null)
            {
                _customOfferItems = CustomCosmetics.Items
                    .Where(item => item.Enabled != false && Guid.TryParse(item.Id, out _))
                    .Select(item => CIItemToItem(new CItem(
                        true, // Purchasable — the panel prices it; unlock-all rules apply the same
                        new CItem.PersonaData(
                            item.PieceType,
                            Guid.Parse(item.PieceId),
                            Guid.Parse(item.Id),
                            "1.0.0",
                            string.IsNullOrWhiteSpace(item.Rarity) ? "common" : item.Rarity,
                            Math.Max(0, item.Price),
                            string.IsNullOrWhiteSpace(item.Creator) ? "KotPasztet" : item.Creator,
                            null // AssetFileName — the binary rides the Contents personabinary entry
                        ),
                        item.Title,
                        string.IsNullOrWhiteSpace(item.Description) ? "§" : item.Description,
                        item.ThumbnailId,
                        DateTime.UtcNow,
                        DateTime.UtcNow,
                        DateTime.UtcNow,
                        Guid.Parse(item.Id),
                        Guid.Parse(item.Id),
                        "B63A0803D3653643",
                        "301F442C3B63DC20",
                        [.. item.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)), "custom", "1P"],
                        new Dictionary<string, CItem.KeywordValues>(),
                        [
                            new CItem.QueryManifestContent(
                                $"{item.PieceId}_personabinary",
                                $"/playfab/persona/{item.Id}.zip",
                                "65535.65535.65535",
                                "1.13.0",
                                [],
                                "personabinary"
                            ),
                        ],
                        [],
                        new Dictionary<string, string>(),
                        new Dictionary<string, string>()
                    )))
                    .ToArray();
                _customOfferStamp = (long)stamp;
                Log.Information("Custom cosmetics offer rebuilt: {Count} item(s) (stamp {Stamp})", _customOfferItems.Length, stamp);
            }

            return _customOfferItems;
        }
    }

    private sealed record LiveOfferCache(
        Item[] Items,
        string[][] ThemeTags,
        Dictionary<Guid, Item> ById,
        Dictionary<Guid, string[]> ThemeTagsById);

    private static long? _liveOfferStamp;
    private static LiveOfferCache? _liveOfferCache;

    private static LiveOfferCache LiveOffer
    {
        get
        {
            long stamp = LivePersonaCatalog.IndexStamp;
            if (_liveOfferStamp != stamp || _liveOfferCache is null)
            {
                var offer = LivePersonaCatalog.GetOffer();
                Item[] items = offer.Items.Select(CIItemToItem).ToArray();
                string[][] themes = [.. offer.ThemeTags.Select(tags => tags.ToArray())];
                var byId = new Dictionary<Guid, Item>(items.Length);
                var themeTagsById = new Dictionary<Guid, string[]>(items.Length);
                for (int i = 0; i < items.Length; i++)
                {
                    byId[items[i].Id] = items[i];
                    themeTagsById[items[i].Id] = themes[i];
                }

                _liveOfferCache = new LiveOfferCache(items, themes, byId, themeTagsById);
                _liveOfferStamp = stamp;
                Log.Information("Live persona offer rebuilt: {Count} item(s) (index stamp {Stamp})", items.Length, stamp);

                // Diagnostic for "cosmetic cannot be equipped": a piece type the
                // committed dressing-room layout does not query has NO tab that
                // would show it, so it can never be browsed or equipped (the
                // layout is Mojang-derived and committed — not patched here).
                var unmapped = items
                    .Select(item => item.DisplayProperties?.PieceType)
                    .Where(pieceType => pieceType is not null
                        && PersonaPieceTypes.CategoryGroupOf(pieceType).StartsWith("persona_", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (unmapped.Count > 0)
                {
                    Log.Warning(
                        "Live persona offer holds {Count} piece type(s) the dressing-room layout does not know ({Types}) — no client tab queries them, so those pieces cannot be browsed or equipped",
                        unmapped.Count, string.Join(", ", unmapped));
                }
            }

            return _liveOfferCache;
        }
    }

    // The live persona offer for one player, after the two per-player levers:
    //
    //  - the pack gate (AUTO-GATING — no flag, defaults ∪ per-player grants):
    //    Minecraft-creator ACCESSORIES serve only from the selected packs — the
    //    allowed set must be non-empty AND one of the piece's theme tags must be
    //    in it, so tagless accessories never serve; persona_skin pieces are
    //    exempt and serve regardless. Non-Minecraft creators (the Apace customs
    //    etc.) are exempt either way.
    //  - the supported-only gate (packs.json "onlySupported", default true):
    //    pieces whose compat verdict is risky drop out (see
    //    PersonaPieceTypes.IsSupportedCosmetic).
    //
    // The per-category size limit is applied by the caller over the whole
    // composed offer (staticdata pieces count too — see ApplyGroupedCategoryLimit),
    // not here. The first N pieces per dressing-room group in offer order win —
    // stable per index stamp, so players don't see the catalog reshuffle between
    // requests.
    private static async Task<IReadOnlyList<Item>> LiveOfferItemsFor(string? playerId, CancellationToken cancellationToken)
    {
        if (!LivePersonaCatalog.IsAvailable)
        {
            return [];
        }

        LiveOfferCache live = LiveOffer;
        IReadOnlySet<string> allowed = await PersonaPackGate.AllowedPackUuidsAsync(playerId, cancellationToken);
        bool onlySupported = PersonaPackGate.OnlySupported;

        string[][] themes = live.ThemeTags;
        var filtered = new List<Item>(live.Items.Length);
        for (int i = 0; i < live.Items.Length; i++)
        {
            Item item = live.Items[i];
            if (item.DisplayProperties?.CreatorName is "Minecraft"
                && item.DisplayProperties?.PieceType is not "persona_skin")
            {
                string[] tags = themes[i];
                if (allowed.Count == 0 || !tags.Any(allowed.Contains))
                {
                    continue; // accessories serve ONLY from the selected packs: an empty allowed set (nothing selected) or no matching theme tag drops them — tagless accessories included; persona_skin pieces are exempt
                }
            }

            if (onlySupported)
            {
                string itemId = item.Id.ToString();
                PersonaPieceTypes.CosmeticGateVerdict compat = PersonaPieceTypes.EvaluateCosmetic(itemId);
                if (!compat.Supported)
                {
                    PersonaPieceTypes.LogCompatVerdictOnce("catalog offer", itemId, compat.Reason);
                    continue;
                }
            }

            filtered.Add(item);
        }

        return filtered;
    }

    // The per-category size limit, counted per dressing-room GROUP (see
    // PersonaPieceTypes — the client renders one tab per group, e.g. outerwear =
    // persona_outerwear + persona_hood, so the old per-piece-type cap let
    // grouped tabs show 2-3x the limit). Runs over the whole composed persona
    // offer in SearchAsync: staticdata persona pieces (Bold Rabbit Feet, the
    // Apace capes, the legacy catalog when the live marketplace is off) count
    // first, live pieces fill their group up to the limit. Only persona pieces
    // are counted — the layout, store config and skin pack entries have no
    // piece type and must keep serving untouched; pieces with a missing/empty
    // piece type get their own shared bucket instead of escaping the cap.
    // Eager on purpose: the OData query may enumerate the source more than
    // once and the counter is stateful.
    private static IReadOnlyList<Item> ApplyGroupedCategoryLimit(IEnumerable<Item> items, int limit)
    {
        var counter = new PersonaPieceTypes.GroupLimitCounter(limit);
        var kept = new List<Item>();
        foreach (Item item in items)
        {
            if (item.ContentType is not "PersonaDurable"
                || counter.Allows(PersonaPieceTypes.CategoryGroupOf(item.DisplayProperties?.PieceType)))
            {
                kept.Add(item);
            }
        }

        return kept;
    }

    // Thematic skin packs (MarketplaceDurableCatalog_V1.2 bundles) mapped from the
    // index, cached per index stamp like the persona pieces.
    private static long? _liveSkinPackStamp;
    private static Item[]? _liveSkinPackItems;
    private static Dictionary<Guid, Item>? _liveSkinPackById;

    private static IReadOnlyList<Item> LiveSkinPackItems
    {
        get
        {
            long stamp = LivePersonaCatalog.IndexStamp;
            if (_liveSkinPackStamp != stamp)
            {
                var source = LivePersonaCatalog.GetSkinPacks();
                _liveSkinPackItems = source.Select(LivePersonaCatalog.BuildSkinPackOfferItem).Select(CIItemToItem).ToArray();
                _liveSkinPackById = _liveSkinPackItems.ToDictionary(item => item.Id);
                _liveSkinPackStamp = stamp;
                Log.Information("Live skin pack offer rebuilt: {Count} pack(s) (index stamp {Stamp})", _liveSkinPackItems.Length, stamp);
            }

            return _liveSkinPackItems ?? [];
        }
    }

    // The live classic skin packs. NOT pack-gated: skins are exempt from pack
    // selection — every indexed live skin pack serves (skins serve regardless,
    // see PersonaPackGate).
    private static IReadOnlyList<Item> LiveSkinPackItemsFor() => LiveSkinPackItems;

    private sealed record CatalogSearchRequest(
        bool Count,
        string Filter,
        string? Select,
        string? OrderBy,
        int? Top,
        int? Skip,
        string Scid
    );

    [HttpPost("Search")]
    public async Task<Results<ContentHttpResult, BadRequest>> SearchAsync()
    {
        var cancellationToken = Request.HttpContext.RequestAborted;

        var request = await Request.Body.AsJsonAsync<CatalogSearchRequest>(cancellationToken);

        if (request is null)
        {
            return TypedResults.BadRequest();
        }

        // Dressing room / shop catalog queries are the main way to find out what the client
        // actually asks for (which content types, tags and piece filters per category) —
        // log what the client searched for, and especially what it searched for in vain.
        Log.Debug("Catalog search: filter={Filter} select={Select} orderBy={OrderBy} top={Top} skip={Skip} count={Count}",
            request.Filter, request.Select, request.OrderBy, request.Top, request.Skip, request.Count);

        IEnumerable<Item> items;
        try
        {
            string filter = request.Filter
                .Replace("platforms/any(tp: tp eq 'android.googleplay' and tp eq 'title.earth')", "platforms/any(tp: tp eq 'android.googleplay') and platforms/any(tp: tp eq 'title.earth')");

                // base offer (staticdata) plus the panel's custom cosmetics (own
                // content — never pack-gated, like staticdata) and, when the live
                // persona marketplace is on, the unlock-all live items — one OData
                // filter over all of it. The pack gate decides which theme-tag packs
                // are on by default and per player: Minecraft accessories serve only
                // when one of their theme tags is in the allowed set (nothing
                // selected = 0 accessories); skins and skin packs are exempt. The
                // grouped per-category limit caps every dressing-room tab over the
                // whole composed offer — staticdata persona pieces and the customs
                // count first, live pieces fill their group up to the limit.
                string? callerId = PlayfabAuth() is { IsB: false } caller ? caller.A.Id : null;
                IReadOnlyList<Item> livePacks = LiveSkinPackItemsFor();
                IEnumerable<Item> source = BaseOfferItems
                    .Concat(CustomOfferItems) // customs count first for the grouped per-category limit
                    .Concat(await LiveOfferItemsFor(callerId, cancellationToken))
                    .Concat(livePacks);

                int categoryLimit = PersonaPackGate.CategoryLimit;
                if (categoryLimit > 0)
                {
                    source = ApplyGroupedCategoryLimit(source, categoryLimit);
                }

            var oDataQuery = source.AsQueryable().OData(settings =>
                {
                    settings.EnableCaseInsensitive = true;
                    settings.ValidationSettings.MaxNodeCount = 10000;
                }, GetEdmModel())
                .Filter(filter);

            if (request.OrderBy is not null)
            {
                oDataQuery = oDataQuery.OrderBy(request.OrderBy);
            }

            var query = oDataQuery.ToOriginalQuery();

            if (request.Skip is { } skip)
            {
                query = query.Skip(skip);
            }

            if (request.Top is { } top)
            {
                query = query.Take(top);
            }

            items = query
                .ToArray()
                .Select(item => WithSkinPackRow(ToResponse(item, Request), livePacks));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Catalog search failed, returning no items (filter: {Filter})", request.Filter);
            items = [];
        }

        var response = new Dictionary<string, object>();

        if (request.Count)
        {
            response["Count"] = items.Count(); // items is empty or select over array, so this is fine
        }

        if (!items.Any())
        {
            Log.Information("Catalog search matched no items: filter={Filter} select={Select} top={Top}",
                request.Filter, request.Select, request.Top);
        }

        response["Items"] = items;
        response["ConfigurationName"] = "DEFAULT";

        Response.Headers.Append("access-control-allow-credentials", "true");
        Response.Headers.Append("access-control-allow-headers", "Content-Type, Content-Encoding, X-Authentication, X-Authorization, X-PlayFabSDK, X-ReportErrorAsSuccess, X-SecretKey, X-EntityToken, Authorization, x-ms-app, x-ms-client-request-id, x-ms-user-id, traceparent, tracestate, Request-Id");
        Response.Headers.Append("access-control-allow-methods", "GET, POST");
        Response.Headers.Append("access-control-allow-origin", "*");

        return TypedResults.Content(JsonSerializer.Serialize(new PlayfabOkResponse(
            200,
            "OK",
            response
        ), jsonOptions), "application/json");
    }

    [HttpPost("SearchStores")]
    public ContentHttpResult SearchStores()
        => JsonPascalCase(new PlayfabOkResponse(
            200,
            "OK",
            new Dictionary<string, object>()
            {
                ["Count"] = 0,
                ["Stores"] = Array.Empty<object>(),
                ["ConfigurationName"] = "DEFAULT",
            }
        ));

    private sealed record GetPublishedItemRequest(
        string? ItemId
    );

    private sealed record GetPublishedItemResponse(
        Item Item
    );

    [HttpPost("GetPublishedItem")]
    public async Task<Results<ContentHttpResult, NotFound, BadRequest>> GetPublishedItem()
    {
        var cancellationToken = Request.HttpContext.RequestAborted;

        var request = await Request.Body.AsJsonAsync<GetPublishedItemRequest>(cancellationToken);

        if (request is null)
        {
            return TypedResults.BadRequest();
        }

        if (!Guid.TryParse(request.ItemId, out var itemId))
        {
            return JsonPascalCase(new PlayfabErrorResponse(
                400,
                "BadRequest",
                "InvalidParams",
                1000,
                "Invalid input parameters",
                new()
                {
                    ["ItemId"] = ["The ItemId field is required."]
                }
            ));
        }

        // live persona offer resolves first (the legacy 1P persona items are replaced
        // by their live twins when the live marketplace is on); the panel's custom
        // cosmetics come next (own content — never pack-gated, the supported-only
        // gate still applies); live skin packs follow (skins are exempt from pack
        // selection — every indexed pack resolves); staticdata stays for
        // everything else (layout, store config, skin packs, buildplates, Apace
        // pieces). A live piece resolves like an unknown id too when the pack gate
        // hides it from this caller — Minecraft-creator accessories need the
        // allowed set non-empty AND one of their theme tags in it (tagless
        // accessories never resolve; persona_skin pieces are exempt) — or its
        // compat verdict is risky (onlySupported).
        string? callerId = PlayfabAuth() is { IsB: false } caller ? caller.A.Id : null;
        IReadOnlyList<Item> livePacks = LiveSkinPackItemsFor();
        HashSet<Guid> servedPackIds = [.. livePacks.Select(pack => pack.Id)];

        LiveOfferCache? live = LivePersonaCatalog.IsAvailable ? LiveOffer : null;
        IReadOnlySet<string> allowed = await PersonaPackGate.AllowedPackUuidsAsync(callerId, cancellationToken);

        if (live is not null && live.ById.TryGetValue(itemId, out var liveItem))
        {
            if (liveItem.DisplayProperties?.CreatorName is "Minecraft"
                && liveItem.DisplayProperties?.PieceType is not "persona_skin"
                && (allowed.Count == 0
                    || !live.ThemeTagsById.TryGetValue(itemId, out string[]? themeTags)
                    || !themeTags.Any(allowed.Contains)))
            {
                return TypedResults.NotFound(); // accessories serve only from the selected packs — tagless accessories never resolve; persona_skin pieces are exempt
            }

            if (PersonaPackGate.OnlySupported)
            {
                PersonaPieceTypes.CosmeticGateVerdict compat = PersonaPieceTypes.EvaluateCosmetic(itemId.ToString());
                if (!compat.Supported)
                {
                    PersonaPieceTypes.LogCompatVerdictOnce("published item", itemId.ToString(), compat.Reason);
                    return TypedResults.NotFound();
                }
            }

            return TypedResults.Content(JsonSerializer.Serialize(new PlayfabOkResponse(
                200,
                "OK",
                new GetPublishedItemResponse(
                    ToResponse(liveItem, Request)
                )
            ), jsonOptions), "application/json");
        }

        if (LivePersonaCatalog.IsAvailable && _liveSkinPackById is not null && servedPackIds.Contains(itemId) && _liveSkinPackById.TryGetValue(itemId, out var livePack))
        {
            return TypedResults.Content(JsonSerializer.Serialize(new PlayfabOkResponse(
                200,
                "OK",
                new GetPublishedItemResponse(
                    WithSkinPackRow(ToResponse(livePack, Request), livePacks)
                )
            ), jsonOptions), "application/json");
        }

        // custom cosmetics resolve like any other item id (their ids are panel
        // GUIDs that never collide with staticdata or the live catalog)
        Item? customItem = CustomOfferItems.FirstOrDefault(candidate => candidate.Id == itemId);
        if (customItem is not null)
        {
            if (PersonaPackGate.OnlySupported)
            {
                PersonaPieceTypes.CosmeticGateVerdict compat = PersonaPieceTypes.EvaluateCosmetic(itemId.ToString());
                if (!compat.Supported)
                {
                    PersonaPieceTypes.LogCompatVerdictOnce("published item", itemId.ToString(), compat.Reason);
                    return TypedResults.NotFound();
                }
            }

            return TypedResults.Content(JsonSerializer.Serialize(new PlayfabOkResponse(
                200,
                "OK",
                new GetPublishedItemResponse(
                    ToResponse(customItem, Request)
                )
            ), jsonOptions), "application/json");
        }

        if (!StaticData.Playfab.Items.TryGetValue(itemId, out var cItem))
        {
            // TODO: fake not found
            return TypedResults.NotFound();
        }

        var item = CIItemToItem(cItem);

        return TypedResults.Content(JsonSerializer.Serialize(new PlayfabOkResponse(
            200,
            "OK",
            new GetPublishedItemResponse(
                WithSkinPackRow(ToResponse(item, Request), livePacks)
            )
        ), jsonOptions), "application/json");
    }

    private static object ConvertContent(object content) => content switch
    {
        CItem.QueryManifestContent qmContent => new Item.QueryManifestContent(
            qmContent.Id, qmContent.Url, qmContent.MaxClientVersion, qmContent.MinClientVersion, qmContent.Tags, qmContent.Type),
        JsonElement { ValueKind: JsonValueKind.Object } je when
                je.TryGetProperty("Id", out var idProp) && idProp.ValueKind == JsonValueKind.String &&
                je.TryGetProperty("Url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String &&
                je.TryGetProperty("Type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
            => new Item.QueryManifestContent(
                idProp.GetString() ?? "",
                urlProp.GetString() ?? "",
                je.TryGetProperty("MaxClientVersion", out var maxProp) && maxProp.ValueKind == JsonValueKind.String ? maxProp.GetString() ?? "" : "65535.65535.65535",
                je.TryGetProperty("MinClientVersion", out var minProp) && minProp.ValueKind == JsonValueKind.String ? minProp.GetString() ?? "" : "1.0.0",
                je.TryGetProperty("Tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array
                    ? tagsProp.EnumerateArray().Select(tag => tag.GetString() ?? "")
                    : [],
                typeProp.GetString() ?? ""),
        _ => content,
    };

    // The dressing room's Classic Skins row (skinPackFeaturedQuery) selects its offers by
    // explicit productIds — the committed layout manifest (a2e12d4d) only knows the two
    // offers that existed when it was written. Skin pack offers added later are tagged
    // "has_skinpack"; append their ids to every served skinPackFeaturedQuery so a new
    // pack is just another item JSON (deduped, ids already in the list are kept in place).
    private static IEnumerable<string> SkinPackOfferIds => StaticData.Playfab.Items.Values
        .Where(item => item.Data is CItem.ClientConfigData { ContentType: "MarketplaceDurableCatalog_V1.2" }
            && item.Tags.Contains("has_skinpack"))
        .Select(item => item.Id.ToString());

    private static JsonElement MergeSkinPackOffersIntoFeaturedQuery(JsonElement displayProperties)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(displayProperties.GetRawText());
        }
        catch (JsonException)
        {
            return displayProperties;
        }

        if (node?["dressingRoomLayout"]?["skinPackFeaturedQuery"]?["productIds"] is not JsonArray productIds)
        {
            return displayProperties;
        }

        foreach (string offerId in SkinPackOfferIds)
        {
            if (!productIds.Any(entry => entry?.GetValue<string>() == offerId))
            {
                productIds.Add(offerId);
            }
        }

        return JsonSerializer.SerializeToElement(node, jsonOptions);
    }

    // The response-time half of the featured-query merge: the staticdata ids were
    // already appended when the cache was mapped (CIItemToItem); this pass appends
    // the LIVE skin pack ids the current caller may see — all of them when no
    // packs are configured, defaults ∪ grants otherwise. Runs per response, never
    // per cache build, because the list is per-player.
    private static Item WithSkinPackRow(Item item, IReadOnlyList<Item> livePacks)
    {
        if (livePacks.Count == 0
            || item.ExtensionData is not { } extension
            || !extension.TryGetValue("DisplayProperties", out var display)
            || display.ValueKind != JsonValueKind.Object)
        {
            return item;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(display.GetRawText());
        }
        catch (JsonException)
        {
            return item;
        }

        if (node?["dressingRoomLayout"]?["skinPackFeaturedQuery"]?["productIds"] is not JsonArray productIds)
        {
            return item;
        }

        var present = new HashSet<string>(
            productIds.Where(entry => entry is not null).Select(entry => entry!.GetValue<string>()),
            StringComparer.OrdinalIgnoreCase);
        foreach (var pack in livePacks)
        {
            string packId = pack.Id.ToString();
            if (present.Add(packId))
            {
                productIds.Add(packId);
            }
        }

        extension["DisplayProperties"] = JsonSerializer.SerializeToElement(node, jsonOptions);
        return item;
    }

    private static Item CIItemToItem(CItem item)
    {
        Item.PriceR? price = item.Data switch
        {
            CItem.BuildplateData data => new Item.PriceR([
                new([
                    new("8b77345d-6250-4321-b3c2-373468b39457", "8b77345d-6250-4321-b3c2-373468b39457", "8b77345d-6250-4321-b3c2-373468b39457", data.Cost),
                ]),
            ], []),
            CItem.InventoryItemData data => new Item.PriceR([
                new([
                    new("8b77345d-6250-4321-b3c2-373468b39457", "8b77345d-6250-4321-b3c2-373468b39457", "8b77345d-6250-4321-b3c2-373468b39457", data.Cost),
                ]),
            ], []),
            // the two currency ids are the ones the built-in Bold Rabbit Feet persona item prices in
            CItem.PersonaData data => new Item.PriceR([
                new([
                    new("ecd19d3c-7635-402c-a185-eb11cb6c6946", "ecd19d3c-7635-402c-a185-eb11cb6c6946", "ecd19d3c-7635-402c-a185-eb11cb6c6946", data.Price),
                ]),
                new([
                    new("0113e233-7637-48e7-91b0-349fdc74713d", "0113e233-7637-48e7-91b0-349fdc74713d", "0113e233-7637-48e7-91b0-349fdc74713d", data.Price),
                ]),
            ], []),
            // client-parsed entries (skin pack offers) price in the same ruby currencies
            CItem.ClientConfigData data => data.Price is { } configPrice ? new Item.PriceR([
                new([
                    new("ecd19d3c-7635-402c-a185-eb11cb6c6946", "ecd19d3c-7635-402c-a185-eb11cb6c6946", "ecd19d3c-7635-402c-a185-eb11cb6c6946", configPrice),
                ]),
                new([
                    new("0113e233-7637-48e7-91b0-349fdc74713d", "0113e233-7637-48e7-91b0-349fdc74713d", "0113e233-7637-48e7-91b0-349fdc74713d", configPrice),
                ]),
            ], []) : null,
            CItem.RubyData => null,
            CItem.QueryManifestData => null,
            _ => throw new UnreachableException(),
        };

        // persona pieces can either bring their own Contents (from the item json) or point at
        // a local file in staticdata/playfab/persona served by the persona cdn endpoint
        IEnumerable<object> contents = item.Contents;
        if (item.Data is CItem.PersonaData { AssetFileName: { } assetFileName })
        {
            contents = [new CItem.QueryManifestContent(
                $"{((CItem.PersonaData) item.Data).PieceId}_personabinary",
                $"/playfab/persona/{assetFileName}",
                "65535.65535.65535",
                "1.0.0",
                [],
                "personabinary"
            )];
        }

        // contents that came from the item json deserialize as raw JsonElement objects
        // (CItem.Contents is IReadOnlyList<object>) and would pass through the response
        // untouched, leaving relative urls the client cannot fetch - convert them to the
        // typed response model so ToResponse can prefix the host
        contents = contents.Select(ConvertContent);

        // client-parsed entries (PersonaQueryManifest_V1.0, StoreConfig_V1.1,
        // MarketplaceDurableCatalog_V1.2) carry their displayProperties verbatim; every
        // other item type gets the typed model below. The one exception: the dressing
        // room layout's skinPackFeaturedQuery.productIds gets skin pack offers merged in
        // (see MergeSkinPackOffersIntoFeaturedQuery).
        Dictionary<string, JsonElement>? extensionData = null;
        Item.DisplayPropertiesR? displayProperties;
        switch (item.Data)
        {
            case CItem.ClientConfigData configData:
                extensionData = new() { ["DisplayProperties"] = configData.ContentType == "PersonaQueryManifest_V1.0"
                    ? MergeSkinPackOffersIntoFeaturedQuery(configData.DisplayProperties)
                    : configData.DisplayProperties };
                displayProperties = null;
                break;
            default:
                displayProperties = BuildDisplayProperties(item);
                break;
        }

        return new Item(
            new Item.Entity(item.SourceEntityId, "namespace", "namespace"),
            new Item.Entity(item.SourceEntityId, "namespace", "namespace"),
            item.Id,
            item.Data switch
            {
                CItem.BuildplateData => "bundle",
                CItem.InventoryItemData => "bundle",
                CItem.PersonaData => "bundle",
                CItem.RubyData => "catalogItem",
                CItem.QueryManifestData => "catalogItem",
                CItem.ClientConfigData data => data.ResponseType,
                _ => throw new UnreachableException(),
            },
            item.FriendlyId is null ? [] : [new("FriendlyId", item.FriendlyId.Value)],
            item.FriendlyId,
            ((IEnumerable<KeyValuePair<string, string>>)[new("NEUTRAL", item.Title), .. item.TitleTranslations, new("neutral", item.Title)])
                .ToDictionary(),
            ((IEnumerable<KeyValuePair<string, string>>)[new("NEUTRAL", item.Description), .. item.DescriptionTranslations, new("neutral", item.Description)])
                .ToDictionary(),
            item.Keywords.ToDictionary(item => item.Key, item => new Item.KeywordValues(item.Value.Values)),
            item.Data switch
            {
                CItem.BuildplateData => "BuildplateOffer",
                CItem.InventoryItemData => "InventoryItemOffer",
                CItem.PersonaData => "PersonaDurable",
                CItem.RubyData => "RubyOffer",
                CItem.QueryManifestData => "GenoaQueryManifest_V0.0.3",
                // the exact content type the client filters on ("PersonaQueryManifest_V1.0",
                // "StoreConfig_V1.1", "MarketplaceDurableCatalog_V1.2")
                CItem.ClientConfigData data => data.ContentType,
                _ => throw new UnreachableException(),
            },
            new Item.Entity(item.CreatorEntityId, "title_player_account", "title_player_account"),
            new Item.Entity(item.CreatorEntityId, "title_player_account", "title_player_account"),
            item.Data switch
            {
                CItem.RubyData or CItem.PersonaData => false, // persona pieces are not stackable, like the built-in one
                CItem.ClientConfigData data => data.IsStackable,
                _ => null, // the real manifest/config entries omit IsStackable entirely
            }, // IsStackable
            item.Data switch
            {
                CItem.BuildplateData => ["android.amazonappstore", "android.googleplay", "b.store", "ios.store", "nx.store", "oculus.store.gearvr", "oculus.store.rift", "uwp.store", "uwp.store.mobile", "xboxone.store", "title.bedrockvanilla", "title.earth"],
                CItem.InventoryItemData => ["android.googleplay", "ios.store", "uwp.store", "title.earth"],
                CItem.PersonaData => ["android.amazonappstore", "android.googleplay", "b.store", "ios.store", "nx.store", "oculus.store.gearvr", "oculus.store.rift", "uwp.store", "uwp.store.mobile", "xboxone.store", "title.bedrockvanilla", "title.earth"],
                CItem.RubyData => ["android.googleplay", "ios.store", "uwp.store", "title.bedrockvanilla", "title.earth"],
                CItem.QueryManifestData => ["android.googleplay", "ios.store", "uwp.store", "title.earth"],
                CItem.ClientConfigData => ["android.amazonappstore", "android.googleplay", "b.store", "ios.store", "nx.store", "oculus.store.gearvr", "oculus.store.rift", "uwp.store", "uwp.store.mobile", "xboxone.store", "title.bedrockvanilla", "title.earth"],
                _ => throw new UnreachableException(),
            },
            item.Tags,
            item.CreationDate,
            item.LastModifiedDate,
            item.StartDate,
            contents.Select(content => content switch
            {
                CItem.QueryManifestContent qmContent => new Item.QueryManifestContent(qmContent.Id, qmContent.Url, qmContent.MaxClientVersion, qmContent.MinClientVersion, qmContent.Tags, qmContent.Type),
                _ => content,
            }),
            item.ThumbnailImageId is null ? [] : [new(item.ThumbnailImageId, "Thumbnail", "Thumbnail", $"/playfab/images/{item.ThumbnailImageId}.png")],
            item.ItemReferences.Select(reference => new Item.ItemReference(reference.Id, reference.Amount)),
            price,
            price,
            [],
            displayProperties
        )
        {
            ExtensionData = extensionData,
        };
    }

    private static Item.DisplayPropertiesR BuildDisplayProperties(CItem item) => item.Data switch
    {
        CItem.BuildplateData data => Item.DisplayPropertiesR.CreateBuildplate(
            "Minecraft",
            data.Cost,
            item.Purchasable,
            data.Rarity.ToString().ToLowerInvariant(),
            [new("entitlement_EarthBuildPlate", data.Id, data.Version)],
            data.Id,
            data.Size.ToString().ToLowerInvariant(),
            data.UnlockLevel
        ),
        CItem.InventoryItemData data => Item.DisplayPropertiesR.CreateInventoryItem(
            data.Cost,
            data.Rarity.ToString(),
            [new("entitlement_InventoryItemOffer", data.Id, data.Version)],
            data.Id,
            data.Amount
        ),
        CItem.RubyData data => Item.DisplayPropertiesR.CreateRuby(
            data.BonusCoinCount,
            data.CoinCount,
            data.OriginalCreatorId,
            data.Sku
        ),
        CItem.QueryManifestData data => Item.DisplayPropertiesR.CreateQueryManifest(
            data.MinClientVersion,
            data.MaxClientVersion,
            data.Tabs.Select(tab => new Item.DisplayPropertiesR.Tab(
                tab.ScreenLayoutQueries.Select(layoutQuery => new Item.DisplayPropertiesR.Tab.ScreenLayoutQuery(
                    // TODO: haven't seen it yet, but it's possible these can have properties
                    layoutQuery.ColumnType is Apace.StaticData.Playfab.Tab.ColumnType.Rectangle ? new object() : null,
                    layoutQuery.ColumnType is Apace.StaticData.Playfab.Tab.ColumnType.Square ? new object() : null,
                    layoutQuery.ColumnType is Apace.StaticData.Playfab.Tab.ColumnType.Grid ? new object() : null,
                    layoutQuery.Queries.Select(query => new Item.DisplayPropertiesR.Tab.ScreenLayoutQuery.Query(
                        query.ProductIds,
                        query.QueryContentTypes.Select(type => type.ToString()),
                        query.TopCount
                    )),
                    layoutQuery.ComponentId
                )),
                tab.TabIcon,
                tab.TabTitle,
                tab.TabId
            )),
            data.GlobalNotSearchQueryTags
        ),
        CItem.PersonaData data => Item.DisplayPropertiesR.CreatePersona(
            data.CreatorName ?? "Minecraft",
            data.Price,
            item.Purchasable,
            data.Rarity,
            [new("persona_piece", data.PieceId, data.Version)],
            data.OfferId,
            data.PieceType
        ),
        _ => throw new UnreachableException(),
    };

    private static Item ToResponse(Item item, HttpRequest request)
    {
        string host = $"{(request.IsHttps ? "https://" : "http://")}{request.Host.Value}";

        return item with
        {
            Images = item.Images.Select(image => image with { Url = host + image.Url, }),
            Contents = item.Contents.Select(content => content switch
            {
                Item.QueryManifestContent qmContent => qmContent with { Url = host + qmContent.Url, },
                _ => content,
            }),
        };
    }

    public static IEdmModel GetEdmModel()
    {
        var builder = new ODataConventionModelBuilder();
        builder.EntitySet<Item>("Item");
        // ExtensionData is the JSON passthrough for client-parsed entries (serialized
        // under the DisplayProperties key); hide it from the OData model
        builder.EntityType<Item>().Ignore(item => item.ExtensionData);

        builder.ComplexType<Item.Entity>();
        builder.ComplexType<Item.AlternateId>();
        builder.ComplexType<Item.KeywordValues>();
        builder.ComplexType<Item.Image>();
        builder.ComplexType<Item.ItemReference>();
        builder.ComplexType<Item.PriceR>();
        builder.ComplexType<Item.PriceR.Price>();
        builder.ComplexType<Item.CurrencyAmount>();
        builder.ComplexType<Item.DisplayPropertiesR>();
        builder.ComplexType<Item.DisplayPropertiesR.Tab>();
        builder.ComplexType<Item.DisplayPropertiesR.Tab.ScreenLayoutQuery>();
        builder.ComplexType<Item.DisplayPropertiesR.Tab.ScreenLayoutQuery.Query>();

        return builder.GetEdmModel();
    }

    private sealed record Item(
        Item.Entity SourceEntity,
        Item.Entity SourceEntityKey,
        Guid Id,
        string Type,
        IEnumerable<Item.AlternateId> AlternateIds,
        Guid? FriendlyId,
        Dictionary<string, string> Title,
        Dictionary<string, string> Description,
        Dictionary<string, Item.KeywordValues> Keywords,
        string ContentType,
        Item.Entity CreatorEntityKey,
        Item.Entity CreatorEntity,
        bool? IsStackable, // TODO: ??? only used for ruby offer, always false
        IEnumerable<string> Platforms,
        IEnumerable<string> Tags,
        DateTime CreationDate,
        DateTime LastModifiedDate,
        DateTime? StartDate,
        IEnumerable<object> Contents,
        IEnumerable<Item.Image> Images,
        IEnumerable<Item.ItemReference> ItemReferences,
        Item.PriceR? Price,
        Item.PriceR? PriceOptions,
        IEnumerable<object> DeepLinks,
        // displayProperties for the client-parsed entries (PersonaQueryManifest_V1.0,
        // StoreConfig_V1.1, MarketplaceDurableCatalog_V1.2) is served verbatim through
        // the ExtensionData dictionary below (serialized under the DisplayProperties
        // key); a JsonElement property named DisplayProperties would collide with the
        // typed property at serialization metadata level. Only one of the two is ever
        // set per item.
        Item.DisplayPropertiesR? DisplayProperties = null,
        string? ETag = null
    )
    {
        // raw displayProperties passthrough, see the constructor comment above
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
        public sealed record Entity(
            string Id,
            string Type,
            string TypeString
        );

        public sealed record AlternateId(
            string Type,
            Guid Value
        );

        public sealed record KeywordValues(
            IEnumerable<string> Values
        );

        public sealed record Image(
            string Id,
            string Tag,
            string Type,
            string Url
        );

        public sealed record ItemReference(
            Guid Id,
            int Amount
        );

        public sealed record PriceR(
            PriceR.Price[] Prices,
            PriceR.Price[] RealPrices
        )
        {
            public sealed record Price(
                CurrencyAmount[] Amounts
            );
        }

        public sealed record CurrencyAmount(
            string CurrencyId,
            string Id,
            string ItemId,
            int Amount
        );

        public sealed record QueryManifestContent(
            string Id,
            string Url,
            string MaxClientVersion,
            string MinClientVersion,
            IEnumerable<string> Tags,
            string Type
        );

        public sealed record PackIdentity(
            [property: JsonPropertyName("type")] string Type,
            [property: JsonPropertyName("uuid")] Guid Uuid,
            [property: JsonPropertyName("version")] string Version
        );

        public sealed record DisplayPropertiesR(
            // query manifest
            [property: JsonPropertyName("minClientVersion")] string? MinClientVersion = null,
            [property: JsonPropertyName("maxClientVersion")] string? MaxClientVersion = null,
            [property: JsonPropertyName("tabs")] IEnumerable<DisplayPropertiesR.Tab>? Tabs = null,
            [property: JsonPropertyName("globalNotSearchQueryTags")] IEnumerable<string>? GlobalNotSearchQueryTags = null,

            // buildplate, inventory item, persona
            [property: JsonPropertyName("price")] int? Price = null,
            [property: JsonPropertyName("rarity")] string? Rarity = null,
            [property: JsonPropertyName("packIdentity")] IEnumerable<PackIdentity>? PackIdentity = null,

            // buildplate, persona
            [property: JsonPropertyName("creatorName")] string? CreatorName = null,
            [property: JsonPropertyName("purchasable")] bool? Purchasable = null,

            // buildplate
            [property: JsonPropertyName("buildPlateId")] Guid? BuildPlateId = null,
            [property: JsonPropertyName("buildPlateSize")] string? BuildPlateSize = null,
            [property: JsonPropertyName("buildPlateUnlockLevel"), JsonNumberHandling(JsonNumberHandling.WriteAsString)] int? BuildPlateUnlockLevel = null,

            // inventory item
            [property: JsonPropertyName("itemId")] Guid? ItemId = null,
            [property: JsonPropertyName("amount")] int? Amount = null,

            // ruby
            [property: JsonPropertyName("bonusCoinCount")] int? BonusCoinCount = null,
            [property: JsonPropertyName("coinCount")] int? CoinCount = null,
            [property: JsonPropertyName("originalCreatorId")] string? OriginalCreatorId = null,
            [property: JsonPropertyName("sku")] string? Sku = null,

            // persona
            [property: JsonPropertyName("offerId")] Guid? OfferId = null,
            [property: JsonPropertyName("pieceType")] string? PieceType = null
        )
        {
            public static DisplayPropertiesR CreateQueryManifest(string minClientVersion, string maxClientVersion, IEnumerable<Tab> tabs, IEnumerable<string> globalNotSearchQueryTags)
                => new DisplayPropertiesR(MinClientVersion: minClientVersion, MaxClientVersion: maxClientVersion, Tabs: tabs, GlobalNotSearchQueryTags: globalNotSearchQueryTags);

            public static DisplayPropertiesR CreateBuildplate(string creatorName, int price, bool purchasable, string rarity, IEnumerable<PackIdentity> packIdentity, Guid buildPlateId, string buildPlateSize, int buildPlateUnlockLevel)
                => new DisplayPropertiesR(CreatorName: creatorName, Price: price, Purchasable: purchasable, Rarity: rarity, PackIdentity: packIdentity, BuildPlateId: buildPlateId, BuildPlateSize: buildPlateSize, BuildPlateUnlockLevel: buildPlateUnlockLevel);

            public static DisplayPropertiesR CreateInventoryItem(int price, string rarity, IEnumerable<PackIdentity> packIdentity, Guid itemId, int amount)
                => new DisplayPropertiesR(Price: price, Rarity: rarity, PackIdentity: packIdentity, ItemId: itemId, Amount: amount);

            public static DisplayPropertiesR CreateRuby(int? bonusCoinCount, int coinCount, string originalCreatorId, string sku)
                => new DisplayPropertiesR(BonusCoinCount: bonusCoinCount, CoinCount: coinCount, OriginalCreatorId: originalCreatorId, Sku: sku);

            public static DisplayPropertiesR CreatePersona(string creatorName, int price, bool purchasable, string rarity, IEnumerable<PackIdentity> packIdentity, Guid offerId, string pieceType)
                => new DisplayPropertiesR(CreatorName: creatorName, Price: price, Purchasable: purchasable, Rarity: rarity, PackIdentity: packIdentity, OfferId: offerId, PieceType: pieceType);

            public sealed record Tab(
              [property: JsonPropertyName("screenLayoutQueries")] IEnumerable<Tab.ScreenLayoutQuery> ScreenLayoutQueries,
              [property: JsonPropertyName("tabIcon")] string TabIcon,
              [property: JsonPropertyName("tabTitle")] string TabTitle,
              [property: JsonPropertyName("tabId")] string TabId
          )
            {
                public sealed record ScreenLayoutQuery(
                    [property: JsonPropertyName("column_rectangle")] object? ColumnRectangle,
                    [property: JsonPropertyName("column_square")] object? ColumnSquare,
                    [property: JsonPropertyName("column_grid")] object? ColumnGrid,
                    [property: JsonPropertyName("queries")] IEnumerable<ScreenLayoutQuery.Query> Queries,
                    [property: JsonPropertyName("componentId")] Guid ComponentId
                )
                {
                    public sealed record Query(
                        [property: JsonPropertyName("productIds")] IEnumerable<string> ProductIds,
                        [property: JsonPropertyName("queryContentTypes")] IEnumerable<string> QueryContentTypes,
                        [property: JsonPropertyName("topCount")] int TopCount
                    );
                }
            }
        }
    }
}
