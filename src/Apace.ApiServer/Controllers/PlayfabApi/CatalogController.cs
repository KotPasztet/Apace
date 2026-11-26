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
[Route("1712756451.playfabapi.com/Catalog")]
[Route("11509.playfabapi.com/Catalog")]
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
                // Mojang's own rabbit-feet thumbnail is not shipped in the repo
                // (no Mojang assets committed — runtime fetch or placeholder);
                // the dressing-room tile shows the repo's own placeholder art.
                [new("e7314d2a-8097-48f0-b0e8-039084a22049", "Thumbnail", "Thumbnail", "/images/dummyItemPreview.png")],
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

    // ---------------------------------------------------------------------------
    // Built-in skin pack offers (code-defined fallback for missing staticdata)
    // ---------------------------------------------------------------------------

    // The committed built-in skin pack offers (staticdata-extras/playfab/items:
    // Standard, Minecraft Default Skins, Apace Skins) as code-defined twins of
    // those item JSONs. staticdata stays the source of truth and serves
    // verbatim whenever it carries a HEALTHY copy of the item; these fill in
    // ids the deployed staticdata is missing AND ids it carries in a defective
    // shape — an operator edit/removal through the panel's file browser, or an
    // install whose staticdata predates the item. The MCE client resolves a
    // downloaded skin pack by searching MarketplaceDurableCatalog_V1.2 for its
    // packIdentity uuid as a TAG (the Default Skins uuid acf11539-… is
    // hardcoded in the client): an empty answer there strands the pack — it
    // imports, but its skins cannot be selected or equipped (the 2026-11-25
    // "some cosmetics can't be equipped" session showed exactly that empty
    // search right after the pack zip downloaded). A twin whose id exists but
    // whose content type/packIdentity tag no longer match resolves nothing in
    // that search just like a missing item — see
    // IsHealthyBuiltinSkinPackTwin. Keep the uuids/tags in lockstep with
    // staticdata-extras/playfab/items/*.json and MojangSkinPackBuilder.
    private static readonly CItem[] BuiltinSkinPackItems =
    [
        BuiltinSkinPackOffer(
            id: "c8a4571e-6df0-4b39-9d2e-1f6fbcad4e03",
            friendlyId: "9a5a67e2-4d3b-4f07-a1c4-88b0e5f2f6aa",
            title: "Standard",
            description: "The classic Minecraft skins - Steve and Alex.",
            thumbnailId: "8b52ee1a-9c4b-4a70-9f6e-2d47a53b1c04",
            packUuid: "c18e65aa-7b21-4637-9b63-8ad63622ef01",
            packVersion: "1.0.0",
            skinCount: 3,
            creatorName: "Minecraft",
            tags: ["c8a4571e-6df0-4b39-9d2e-1f6fbcad4e03", "c18e65aa-7b21-4637-9b63-8ad63622ef01", "skinpack", "has_skinpack", "1P", "tag.cat"],
            contentsUrl: null, // the vanilla pack is built into the client — no skinbinary to download
            itemReferenceId: "7d5e5f1c-8b3a-4a2e-9c6d-0e1b2a3c4d5e",
            created: new(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc)),
        BuiltinSkinPackOffer(
            id: "90860f69-bcc5-46c4-9e76-b56b9db5d729",
            friendlyId: "bb111cab-b44d-46cc-b0c4-2316fcf4a494",
            title: "Minecraft Default Skins",
            description: "The nine Minecraft default skins - Steve, Alex, Sunny, Kai, Makena, Zuri, Efe, Ari and Noor.",
            thumbnailId: "6192ef59-f090-423d-8471-90ddeaff3624",
            packUuid: "acf11539-f3ca-4ac6-b3a9-fdd1c2cf3806", // the uuid the MCE client hardcodes its Classic Skins search on
            packVersion: "1.0.2",
            skinCount: 9,
            creatorName: "Minecraft",
            tags: ["90860f69-bcc5-46c4-9e76-b56b9db5d729", "acf11539-f3ca-4ac6-b3a9-fdd1c2cf3806", "skinpack", "has_skinpack", "1P", "tag.cat"],
            contentsUrl: "/playfab/skinpacks/90860f69-bcc5-46c4-9e76-b56b9db5d729.zip", // built into the data volume by MojangSkinPackBuilder
            itemReferenceId: "6192ef59-f090-423d-8471-90ddeaff3624",
            created: new(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc)),
        BuiltinSkinPackOffer(
            id: "42a5f927-0369-47f4-b271-e64e814fa47f",
            friendlyId: "ca74c960-ad18-461c-9355-2ca210fef580",
            title: "Apace Skins",
            description: "A custom skin pack by KotPasztet!",
            thumbnailId: "4ef41e47-cba1-4358-a84f-2d265c286f0f",
            packUuid: "3724ccc4-f959-41f5-a877-6cad824b08b7",
            packVersion: "1.0.0",
            skinCount: 1,
            creatorName: "KotPasztet",
            tags: ["42a5f927-0369-47f4-b271-e64e814fa47f", "3724ccc4-f959-41f5-a877-6cad824b08b7", "skinpack", "has_skinpack", "tag.cat"],
            contentsUrl: "/playfab/skinpacks/42a5f927-0369-47f4-b271-e64e814fa47f.zip",
            itemReferenceId: "4f8e8e19-3218-4614-b0ff-3cd97e173f48",
            created: new(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc)),
    ];

    // One CItem shaped exactly like the staticdata-extras item JSON it twins
    // (MarketplaceDurableCatalog_V1.2 bundle, verbatim displayProperties,
    // skinbinary Contents pointing at the SkinPackCdnController proxy path).
    private static CItem BuiltinSkinPackOffer(string id, string friendlyId, string title, string description, string thumbnailId,
        string packUuid, string packVersion, int skinCount, string creatorName, string[] tags, string? contentsUrl,
        string itemReferenceId, DateTime created)
    {
        var displayProperties = JsonSerializer.SerializeToElement(new
        {
            creatorName,
            offerId = friendlyId,
            price = 0,
            purchasable = true,
            packIdentity = new[] { new { type = "skinpack", uuid = packUuid, version = packVersion } },
            skinPack = new { count = skinCount },
        });

        return new CItem(
            true, // Purchasable — every built-in offer is free
            new CItem.ClientConfigData(
                "MarketplaceDurableCatalog_V1.2",
                "bundle",
                "1.0.0",
                "65535.65535.65535",
                false, // IsStackable
                0, // Price — 0 = free
                displayProperties
            ),
            title,
            description,
            thumbnailId,
            created,
            created,
            new(2019, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            Guid.Parse(id),
            Guid.Parse(friendlyId),
            "B63A0803D3653643",
            "301F442C3B63DC20",
            tags,
            new Dictionary<string, CItem.KeywordValues>(),
            contentsUrl is null
                ? []
                : [new CItem.QueryManifestContent(friendlyId, contentsUrl, "65535.65535.65535", "1.0.0", [], "skinbinary")],
            [new CItem.ItemReference(Guid.Parse(itemReferenceId), 1)],
            new Dictionary<string, string>(),
            new Dictionary<string, string>()
        );
    }

    // The fallback view: built-in offers the deployed staticdata does not serve
    // correctly — the id is gone (an operator edit/removal, or an install whose
    // staticdata predates the item) or the twin EXISTS but is defective (not a
    // MarketplaceDurableCatalog_V1.2 entry, no packIdentity uuid tag, or
    // displayProperties no longer advertising that packIdentity). The
    // defective-twin case is the "present but wrong" gap the plain id-presence
    // guard used to miss: the client's tag search resolved NOTHING because the
    // broken copy matched no search, while the fallback was skipped — the
    // "imports, but its skins cannot be selected or equipped" failure from the
    // 2026-11-25 session. Defective twins are excluded from every serve path
    // (BaseOfferItems, GetPublishedItem, SkinPackOfferIds — see
    // DefectiveBuiltinSkinPackIds) and replaced by the code-defined offer.
    // Mapped once per run; reads the staticdata dictionary — never itemData:
    // this can run inside the CatalogController static constructor
    // (CIItemToItem → MergeSkinPackOffersIntoFeaturedQuery →
    // SkinPackOfferIds), while itemData is still being assigned. An active
    // fallback is logged once per id.
    private static Item[]? _builtinSkinPackFallbackItems;
    private static IReadOnlySet<Guid>? _defectiveBuiltinSkinPackIds;

    private static void EnsureBuiltinSkinPackFallbacks()
    {
        if (_builtinSkinPackFallbackItems is not null)
        {
            return;
        }

        List<Item> fallback = [];
        HashSet<Guid> defective = [];
        foreach (CItem builtin in BuiltinSkinPackItems)
        {
            bool missing = !StaticData.Playfab.Items.TryGetValue(builtin.Id, out CItem? twin);
            if (missing || !IsHealthyBuiltinSkinPackTwin(twin!, builtin))
            {
                defective.Add(builtin.Id);
                Log.Information(
                    "Built-in skin pack offer {Id} ({Title}) is {State} in the deployed staticdata — serving the code-defined fallback offer so the client's skin pack search (pack uuid tag {Tag}) resolves",
                    builtin.Id, builtin.Title, missing ? "missing" : "defective", builtin.Tags[1]);
                fallback.Add(CIItemToItem(builtin));
            }
        }

        _defectiveBuiltinSkinPackIds = defective;
        _builtinSkinPackFallbackItems = [.. fallback];
    }

    private static Item[] BuiltinSkinPackFallbackItems
    {
        get
        {
            EnsureBuiltinSkinPackFallbacks();
            return _builtinSkinPackFallbackItems!;
        }
    }

    // ids of the built-in skin pack offers the deployed staticdata carries in a
    // defective shape (see BuiltinSkinPackFallbackItems) — those twins are
    // replaced by the code-defined offer at every serve path
    private static IReadOnlySet<Guid> DefectiveBuiltinSkinPackIds
    {
        get
        {
            EnsureBuiltinSkinPackFallbacks();
            return _defectiveBuiltinSkinPackIds!;
        }
    }

    // A staticdata twin of a built-in skin pack offer must still be the shape
    // the client's pack search resolves: a MarketplaceDurableCatalog_V1.2 entry
    // whose tags carry the packIdentity uuid (Tags[1] — every BuiltinSkinPackOffer
    // puts the pack uuid tag at index 1, right after the item's own id tag) and
    // whose displayProperties still advertise that same packIdentity uuid.
    // Anything else matches no client search and strands the pack.
    private static bool IsHealthyBuiltinSkinPackTwin(CItem twin, CItem builtin)
    {
        if (twin.Data is not CItem.ClientConfigData { ContentType: "MarketplaceDurableCatalog_V1.2" } config
            || config.DisplayProperties.ValueKind != JsonValueKind.Object
            || !twin.Tags.Contains(builtin.Tags[1]))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(config.DisplayProperties.GetRawText());
            if (!document.RootElement.TryGetProperty("packIdentity", out JsonElement packIdentity)
                || packIdentity.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement entry in packIdentity.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("uuid", out JsonElement uuid)
                    && uuid.ValueKind == JsonValueKind.String
                    && string.Equals(uuid.GetString(), builtin.Tags[1], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false; // unparseable displayProperties — the client cannot resolve this twin
        }
    }

    // The offer's base: the frozen 2021 staticdata catalog minus every 1P
    // "Minecraft" PersonaDurable piece — UNCONDITIONALLY, live index or not.
    // Staticdata carries no pack tags, so none of those accessories can ever
    // pass the pack gate; keeping them while the live index is missing leaked
    // ~45 gated accessories per dressing-room category. The hardcoded Bold
    // Rabbit Feet (the shop needs it) and the Apace custom pieces (creator
    // KotPasztet, never gated) stay; the Default Skins pack entries are
    // non-PersonaDurable and pass untouched. When a synced catalog exists, the
    // live catalog replaces the dropped 1P pieces (see LiveOfferItemsFor).
    private static IEnumerable<Item> BaseOfferItems
    {
        get
        {
            // a defective built-in skin pack twin serves NOWHERE — the code-defined
            // fallback offer replaces it in the composed search source below
            IReadOnlySet<Guid> defectiveBuiltinTwins = DefectiveBuiltinSkinPackIds;
            return itemData.Where(item =>
                !defectiveBuiltinTwins.Contains(item.Id) &&
                (item.ContentType != "PersonaDurable" ||
                item.Id == BoldRabbitFeetItemId ||
                item.DisplayProperties?.CreatorName is not "Minecraft"));
        }
    }

    // Mapped live offer items, cached per index version (the dressing room fires
    // many searches; mapping ~10k items per request would dominate the request).
    // One immutable record per index stamp: the items, their gating theme tags
    // (aligned by index) and the id maps the pack gate and GetPublishedItem
    // resolve against.
    // Custom cosmetics (panel-defined, hot-reloaded from the volume) mapped to
    // offer items, cached per store stamp like the live offer per index stamp.
    // They never enter the pack-gated live offer (own source), so the pack gate
    // never sees them; the onlySupported compat gate still applies via
    // PersonaPieceTypes.
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
    //    ALL live ACCESSORIES serve only from the selected packs, whatever their
    //    marketplace creator — the allowed set must be non-empty AND one of the
    //    piece's theme tags must be in it, so tagless accessories never serve;
    //    persona_skin pieces are exempt and serve regardless (customs never
    //    enter this live offer at all).
    //  - the supported-only gate (packs.json "onlySupported", default true):
    //    pieces whose compat verdict is risky drop out (see
    //    PersonaPieceTypes.IsSupportedCosmetic).
    //  - hidden pieces skip the offer per request — pieces without a binary on
    //    disk (probed dead, stale verdicts, not yet mirrored) must not list:
    //    the client would hang their download on "loading" and then render
    //    nothing (see LivePersonaCatalog.GetHiddenItemIds).
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
        HashSet<string> dead = LivePersonaCatalog.GetHiddenItemIds();
        var filtered = new List<Item>(live.Items.Length);
        int passed = 0, dropped = 0, deadSkipped = 0, filled = 0;
        for (int i = 0; i < live.Items.Length; i++)
        {
            Item item = live.Items[i];
            // face-craft categories (eyes, hair, mouth, facial hair, face items,
            // skins/skin colors) are always allowed — never gated by the pack
            // theme selection, exactly as never by the supported-only gate below.
            // The dead-binary hiding applies to them like to everything else
            // (user's order: a binary-less face piece lists but its equip hangs
            // the client's import — hide it, the tab shows only working pieces).
            if (dead.Contains(item.Id.ToString()))
            {
                deadSkipped++;
                continue; // no binary on disk (probed dead or not yet mirrored) — hide it from searches instead of hanging the client's download
            }

            bool faceCraft = PersonaPieceTypes.IsAlwaysAllowedFaceCraftPieceType(item.DisplayProperties?.PieceType);
            if (!faceCraft)
            {
                string[] tags = themes[i];
                if (allowed.Count == 0 || !tags.Any(allowed.Contains))
                {
                    dropped++;
                    continue; // accessories serve ONLY from the selected packs: an empty allowed set (nothing selected) or no matching theme tag drops them — tagless accessories included; persona_skin pieces are exempt
                }
            }

            passed++;
            if (onlySupported && !faceCraft)
            {
                string itemId = item.Id.ToString();
                PersonaPieceTypes.CosmeticGateVerdict compat = PersonaPieceTypes.EvaluateCosmetic(
                    itemId, item.DisplayProperties?.PieceType,
                    allowRigRepair: PersonaPackGate.ServeTimeTransformActive,
                    hideCustomSkeleton: PersonaPackGate.FlatOnlyActive);
                if (!compat.Supported)
                {
                    PersonaPieceTypes.LogCompatVerdictOnce("catalog offer", itemId, compat.Reason);
                    continue;
                }
            }

            filtered.Add(item);
        }

        // Blank-slot filler (packs.json "fillBlankSlotsWithRandomCosmetics",
        // default on): append the deterministic candidate pool AFTER the
        // pack-selected pieces. The grouped per-category limiter over the whole
        // composed offer keeps the first N per group — staticdata pieces and
        // customs count first, then pack-selected live pieces, then filler —
        // so every group fills up to the limit with the same pieces every
        // build, nothing a player equips reshuffles, and with no packs selected
        // every group fills completely. Nothing fills when the limit is 0.
        if (PersonaPackGate.FillBlankSlots && PersonaPackGate.CategoryLimit > 0)
        {
            RefreshFiller();
            var seen = new HashSet<Guid>();
            foreach (Item item in filtered)
            {
                seen.Add(item.Id);
            }

            foreach (Item candidate in _fillerCandidates!)
            {
                if (seen.Add(candidate.Id))
                {
                    filtered.Add(candidate);
                    filled++;
                }
            }
        }

        (IReadOnlyCollection<string> defaultPacks, int grants) = await PersonaPackGate.AllowedCompositionAsync(playerId, cancellationToken);
        Log.Debug("Live offer: caller={Caller} defaults={Defaults} [{Ids}] grants={Grants} offer={Offer} passed={Passed} dropped={Dropped} dead={Dead} filled={Filled} limit={Limit}",
            playerId ?? "-", defaultPacks.Count, string.Join(",", defaultPacks), grants, live.Items.Length, passed, dropped, deadSkipped, filled, PersonaPackGate.CategoryLimit);

        return filtered;
    }

    // ---------------------------------------------------------------------------
    // Blank-slot filler (packs.json "fillBlankSlotsWithRandomCosmetics", default on)
    // ---------------------------------------------------------------------------

    // The deterministic filler candidate pool ("Fill blank slots with random
    // cosmetics", packs.json fillBlankSlotsWithRandomCosmetics, default ON):
    // every live accessory (non-skin persona piece) that has a binary on disk
    // and — while onlySupported is on — is compat-supported, sorted by item id
    // so the SAME pieces fill the same slots every offer build. The pool draws
    // from the WHOLE mirrored catalog, not just the selected bundles — the
    // warm-up mirrors every accessory while the knob is on (the binary-download
    // scope follows the same switch), so blank slots fill with random cosmetics
    // exactly as the option promises. With the knob OFF the pool is empty.
    // LiveOfferItemsFor appends the pool AFTER the pack-selected pieces and the
    // grouped per-category limiter keeps the first N per group (staticdata
    // pieces and customs count first, then pack-selected live pieces, then
    // filler): every group fills up to the limit, pack pieces are never
    // displaced. Refreshed per (index stamp, packs.json stamp, hidden-set size).
    private static long _fillerStamp;
    private static Item[]? _fillerCandidates;
    private static IReadOnlySet<string> _fillerItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every live piece id the filler pool can offer — the CDN proxy
    /// and GetPublishedItem allow these ids so filler pieces download like any
    /// offered piece (empty while the filler is off or nothing qualifies).</summary>
    public static IReadOnlySet<string> FillerItemIds
    {
        get
        {
            RefreshFiller();
            return _fillerItemIds;
        }
    }

    private static void RefreshFiller()
    {
        long stamp = HashCode.Combine(LivePersonaCatalog.IndexStamp, PersonaPackGate.ConfigStamp, LivePersonaCatalog.GetHiddenItemIds().Count);
        if (_fillerStamp == stamp && _fillerCandidates is not null)
        {
            return;
        }

        bool onlySupported = PersonaPackGate.OnlySupported;
        HashSet<string> dead = LivePersonaCatalog.GetHiddenItemIds();
        bool fillEnabled = PersonaPackGate.FillBlankSlots;
        LiveOfferCache live = LiveOffer;
        var candidates = new List<Item>(live.Items.Length);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < live.Items.Length; i++)
        {
            Item item = live.Items[i];
            string? pieceType = item.DisplayProperties?.PieceType;
            if (pieceType is not "persona_skin"
                && (pieceType is null || !pieceType.StartsWith("persona_", StringComparison.Ordinal)))
            {
                continue; // skins serve regardless of gating; emotes/other content types are not dressing-room pieces
            }

            if (pieceType is not "persona_skin")
            {
                if (!fillEnabled)
                {
                    continue; // the knob is off — nothing fills
                }

                if (dead.Contains(item.Id.ToString()))
                {
                    continue; // no binary on disk — its download would hang the client, never fill with it
                }
            }
            else
            {
                continue; // skins are not filler — they always serve via the pack-free lane already
            }

            if (onlySupported && !PersonaPieceTypes.IsAlwaysAllowedFaceCraftPieceType(pieceType)
                && !PersonaPieceTypes.EvaluateCosmetic(
                    item.Id.ToString(), pieceType,
                    allowRigRepair: PersonaPackGate.ServeTimeTransformActive,
                    hideCustomSkeleton: PersonaPackGate.FlatOnlyActive).Supported)
            {
                continue; // the supported-only gate applies to filler exactly like to pack-selected pieces — face-craft categories excepted (always allowed)
            }

            candidates.Add(item);
            ids.Add(item.Id.ToString());
        }

        candidates.Sort((a, b) => string.CompareOrdinal(a.Id.ToString(), b.Id.ToString()));
        _fillerCandidates = [.. candidates];
        _fillerItemIds = ids;
        _fillerStamp = stamp;
        // mirror the pool into the binary-fetch pipeline's exemption set — the
        // CDN proxy and GetPublishedItem allow these ids, and with this the lazy
        // fetch does too (previously filler pieces outside the selected packs
        // were gate-refused there and always stubbed)
        PersonaPackGate.FillerExemptItemIds.Clear();
        PersonaPackGate.FillerExemptItemIds.UnionWith(ids);
        Log.Information("Blank-slot filler pool rebuilt: {Count} candidate piece(s) (filler {FillState}, index stamp {IndexStamp}, config stamp {ConfigStamp})",
            _fillerCandidates.Length, fillEnabled ? "ON" : "OFF", LivePersonaCatalog.IndexStamp, PersonaPackGate.ConfigStamp);
    }

    // The per-category size limit, counted per dressing-room GROUP (see
    // PersonaPieceTypes — the client renders one tab per group, e.g. outerwear =
    // persona_outerwear + persona_hood, so the old per-piece-type cap let
    // grouped tabs show 2-3x the limit). Runs over the whole composed persona
    // offer in SearchAsync: staticdata persona pieces (Bold Rabbit Feet, the
    // the legacy catalog when the live marketplace is off) count
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
            // "platforms/any(tp: tp eq 'X' and tp eq 'title.earth')" can never match (one value can't equal two
            // strings), so the clients' platform filter (android.googleplay, ios.store, ...) is split into two
            // separate any() checks before the filter is evaluated
            string filter = System.Text.RegularExpressions.Regex.Replace(
                request.Filter,
                @"platforms/any\(tp:\s*tp eq '([^']+)'\s+and\s+tp eq '([^']+)'\)",
                "platforms/any(tp: tp eq '$1') and platforms/any(tp: tp eq '$2')");

                // base offer (staticdata) plus the panel's custom cosmetics (own
                // content — never pack-gated, like staticdata) and, when the live
                // persona marketplace is on, the unlock-all live items — one OData
                // filter over all of it. The pack gate decides which theme-tag packs
                // are on by default and per player: live accessories serve only
                // when one of their theme tags is in the allowed set (nothing
                // selected = 0 accessories); skins and skin packs are exempt. The
                // grouped per-category limit caps every dressing-room tab over the
                // whole composed offer — staticdata persona pieces and the customs
                // count first, live pieces fill their group up to the limit.
                string? callerId = PlayfabAuth() is { IsB: false } caller ? caller.A.Id : null;
                IReadOnlyList<Item> livePacks = LiveSkinPackItemsFor();
                IEnumerable<Item> source = BaseOfferItems
                    .Concat(BuiltinSkinPackFallbackItems) // built-in skin pack offers missing from staticdata — the client's Classic Skins searches must resolve them
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

        // GetPublishedItem is the DOWNLOAD path, not the offer: the frozen client
        // resolves this endpoint for a piece it is about to download/equip. Every
        // visibility gate (pack selection, onlySupported compat, probe-dead)
        // already shapes what the OFFER advertises (Catalog/Search + the
        // blank-slot filler pool), so a client that never saw an item in its offer
        // never asks for it here. An item the client HAS — granted by an earlier
        // offer, cached receipts, or its own persona profile — must ALWAYS
        // resolve: a 404 here strands the equip on the "Wczytywanie…" loading
        // screen (standing rule: restrict by not delivering; never block
        // download/equip of an already-granted item). What actually ships is
        // decided by the binary endpoint: mirrored files serve, an entitled live
        // fetch downloads, everything else returns the deterministic placeholder
        // (imports instantly, renders nothing) — a piece uuid never 404s there.
        IReadOnlyList<Item> livePacks = LiveSkinPackItemsFor();
        HashSet<Guid> servedPackIds = [.. livePacks.Select(pack => pack.Id)];
        LiveOfferCache? live = LivePersonaCatalog.IsAvailable ? LiveOffer : null;

        if (live is not null && live.ById.TryGetValue(itemId, out var liveItem))
        {
            return TypedResults.Content(JsonSerializer.Serialize(new PlayfabOkResponse(
                200,
                "OK",
                new GetPublishedItemResponse(
                    ToResponse(liveItem, Request)
                )
            ), jsonOptions), "application/json");
        }

        // not in the current offer — a cached client may still hold an entry that
        // probed dead, is emote-only or otherwise offer-hidden since; resolve it
        // from the raw index so the piece still downloads (a stub for a dead
        // binary) instead of 404ing
        if (LivePersonaCatalog.Enabled
            && LivePersonaCatalog.GetIndexEntry(itemId.ToString()) is { } rawEntry)
        {
            return TypedResults.Content(JsonSerializer.Serialize(new PlayfabOkResponse(
                200,
                "OK",
                new GetPublishedItemResponse(
                    ToResponse(CIItemToItem(LivePersonaCatalog.BuildOfferItem(rawEntry)), Request)
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
        // GUIDs that never collide with staticdata or the live catalog) — with
        // no gate here either: the supported-only gate filters them out of the
        // OFFER (and new grants), while an already-granted cosmetic must stay
        // downloadable even if its compat verdict later changed
        Item? customItem = CustomOfferItems.FirstOrDefault(candidate => candidate.Id == itemId);
        if (customItem is not null)
        {
            return TypedResults.Content(JsonSerializer.Serialize(new PlayfabOkResponse(
                200,
                "OK",
                new GetPublishedItemResponse(
                    ToResponse(customItem, Request)
                )
            ), jsonOptions), "application/json");
        }

        // a defective built-in skin pack twin resolves as the code-defined offer —
        // serving the broken staticdata shape here would strand the client's pack
        // resolve exactly like the empty tag search does
        if (DefectiveBuiltinSkinPackIds.Contains(itemId)
            && BuiltinSkinPackFallbackItems.FirstOrDefault(candidate => candidate.Id == itemId) is { } repairedItem)
        {
            return TypedResults.Content(JsonSerializer.Serialize(new PlayfabOkResponse(
                200,
                "OK",
                new GetPublishedItemResponse(
                    WithSkinPackRow(ToResponse(repairedItem, Request), livePacks)
                )
            ), jsonOptions), "application/json");
        }

        if (!StaticData.Playfab.Items.TryGetValue(itemId, out var cItem))
        {
            // last resolve lane: the code-defined built-in skin pack offers the
            // deployed staticdata does not carry (an operator edit/removal, or
            // an older install) — same standing rule as above: a client that
            // knows the item id (featured query, cached offer) must always
            // resolve, a 404 here strands the equip
            Item? builtinItem = BuiltinSkinPackFallbackItems.FirstOrDefault(candidate => candidate.Id == itemId);
            if (builtinItem is not null)
            {
                return TypedResults.Content(JsonSerializer.Serialize(new PlayfabOkResponse(
                    200,
                    "OK",
                    new GetPublishedItemResponse(
                        WithSkinPackRow(ToResponse(builtinItem, Request), livePacks)
                    )
                ), jsonOptions), "application/json");
            }

            // TODO: fake not found
            return TypedResults.NotFound();
        }

        var item = CIItemToItem(cItem);

        // no pack/creator gate here either: staticdata 1P "Minecraft" accessories
        // are excluded from the OFFER unconditionally (BaseOfferItems), so they
        // are never newly granted — but a client that cached one from an earlier
        // session still receives a receipt entitlement for it and must be able
        // to resolve and download the piece (the binary endpoint answers the
        // proxy URL with the real mirrored file or the importable placeholder)

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
        // defective built-in twins are replaced by the code-defined fallback offers,
        // whose ids the Concat below appends — never list the broken copy itself
        .Where(item => !DefectiveBuiltinSkinPackIds.Contains(item.Id)
            && item.Data is CItem.ClientConfigData { ContentType: "MarketplaceDurableCatalog_V1.2" }
            && item.Tags.Contains("has_skinpack"))
        .Select(item => item.Id.ToString())
        // the code-defined fallback offers (ids the deployed staticdata does
        // not carry or carries in a defective shape) join the featured row the
        // same way — see BuiltinSkinPackFallbackItems; the merge below dedupes
        .Concat(BuiltinSkinPackFallbackItems.Select(item => item.Id.ToString()));

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

        // Cache identity for the serve-time rig rewriter (repair / strip): a
        // piece whose verdict passed ONLY because a transform is active (custom
        // bones were its sole risk signal) carries the rewriter marker in its
        // catalog ETag, so its identity differs from the unrepaired bytes and
        // clients re-download after the admin flips a knob. Deterministic per
        // knob state — stable across restarts while the knobs are unchanged;
        // null (as before) for everything else. The same marker is served on
        // the binary response (PersonaCdnController / PersonaRigRepairer).
        string? etag = item.DisplayProperties?.PieceType is { } pieceType
            && PersonaPackGate.ServeTimeTransformActive
            && PersonaPieceTypes.EvaluateCosmetic(item.Id.ToString(), pieceType, allowRigRepair: true) is { Supported: true, RepairedRig: true }
                ? PersonaRigRepairer.CurrentServeMarker
                : item.ETag;

        return item with
        {
            ETag = etag,
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
