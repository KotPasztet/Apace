using Serilog;
using Apace.Common.Utils;
using Apace.DB;
using Apace.DB.Models.Player;

namespace Apace.ApiServer.Utils;

/// <summary>
/// Credits season challenge progress for things that happen inside a running buildplate/adventure instance
/// (the launcher reports them to the API server over the event bus).
/// </summary>
internal static class ChallengeEvents
{
    // season challenge "play 2 adventures" (rewards 30 arrows)
    private const string AdventuresChallengeId = "e81d6ce8-622c-461a-a2d5-bb1f8ac36c62";
    private const string AdventuresReferenceId = "10986d40-516f-459b-bc24-a16296998c1e";
    private const int AdventuresThreshold = 2;

    // season challenge "place a chicken and a cow on a buildplate" (rewards 1 shears)
    private const string PlaceAnimalsChallengeId = "1ccbe15e-3a72-4d8b-ad0b-00a23ac72eb1";
    private const string PlaceAnimalsReferenceId = "57f50f14-1b46-460b-82dc-e1220d53a15b";
    private const int PlaceAnimalsThreshold = 2;

    // season challenge "defeat 2 spiders with a sword" (rewards 1 bucket)
    private const string SpiderChallengeId = "8e052786-0495-4eb9-80bf-e1b36b9d89ef";
    private const string SpiderReferenceId = "f022ad24-44e4-484e-b839-77237bb3d1b8";

    // season challenge "place 2 different types of saplings on a buildplate" (rewards 3 cod)
    private const string SaplingsChallengeId = "d8d2abd5-f318-4c34-a257-57b9691a2774";
    private const string SaplingsReferenceId = "bfd00245-4801-46fa-af1a-28979df23377";

    // season challenge "collect 2 different types of leaves with shears" (rewards 10 TNT)
    private const string LeavesChallengeId = "242b6706-8112-4302-819b-bde98fd574f6";
    private const string LeavesReferenceId = "543a0397-25c5-4e2d-b39d-6d6287c7cbed";

    // season challenge "collect 8 oak logs from adventures" (rewards 1 iron axe)
    private const string OakLogsChallengeId = "761c6754-ad1f-4cbf-a116-8a1e1dd611b9";
    private const string OakLogsReferenceId = "09e5a7ea-c4a7-4401-af88-dcb8b4f47abe";

    // season challenge "place an uncommon mob on a buildplate" (rewards 1 gold pickaxe)
    private const string PlaceUncommonMobChallengeId = "d434c853-7fab-4ac7-b64b-80fb6dd9ddd9";
    private const string PlaceUncommonMobReferenceId = "e02e6a5d-8541-4d34-a2ee-9bcdcc3381a4";

    // season challenge "defeat 4 hostile mobs with a gold pickaxe" (rewards 1 uncommon adventure crystal)
    private const string HostileGoldPickaxeChallengeId = "fb13127e-d19d-48bd-a977-f830eecff180";
    private const string HostileGoldPickaxeReferenceId = "92ed3b3a-a132-44c8-8045-2d6d828c0177";

    // season challenge "Super prizes unlocked" (rewards 250 xp): has no task of its own, it completes by itself once the
    // mid-season checkpoint above is completed
    private const string SuperPrizesChallengeId = "96593763-a8a6-41f8-986a-8d6df0470c57";
    private const string SuperPrizesReferenceId = "0688f294-76a8-41ed-a34c-24b8d9e3bc98";

    // season challenge "collect 3 different colors of terracotta for your journal" (rewards 1 mining boost II)
    private const string TerracottaChallengeId = "63e5d65e-1152-4adc-97e7-2818854a867a";
    private const string TerracottaReferenceId = "2a599738-efe0-45bf-8fec-1ff73b25f374";

    // season challenge "collect 8 gold ore from adventures" (rewards 1 attack boost II)
    private const string GoldOreChallengeId = "8ff3a6c3-0a1a-4343-8672-7f559cc8918d";
    private const string GoldOreReferenceId = "d4a304cf-d6b5-4fcd-862c-8d7f3418443e";

    // season challenge "place a pale pig, parrot and salmon on a buildplate" (rewards 1 glow squid)
    private const string PlaceThreeMobsChallengeId = "2bb26b41-e841-4815-ade5-a0d93ac51258";
    private const string PlaceThreeMobsReferenceId = "b8f5af67-ff23-4c1d-95a9-f6ccff5137e0";

    // season challenge "defeat 7 hostile mobs with TNT" (rewards 1 polar bear)
    private const string HostileTntChallengeId = "dbdb4592-4211-4ffe-a76c-8a3a2213c93c";
    private const string HostileTntReferenceId = "af6d9346-4a07-458e-880e-3f3601db8739";

    // season challenge "place a squid and a glow squid on a buildplate" (rewards 1 rare adventure crystal)
    private const string PlaceSquidsChallengeId = "abd271f8-22cd-42ff-b985-cd5e642ea25a";
    private const string PlaceSquidsReferenceId = "5a6fbcce-2d2d-47f9-b36e-4d34c351f8a3";

    // season challenge "collect 2 raw salmon from a buildplate" (rewards 20 rubies)
    private const string SalmonChallengeId = "29ebe650-072f-4f70-996f-4ffdda93ed1f";
    private const string SalmonReferenceId = "1282801b-a5a8-4339-a587-b16d31468b55";

    // season challenge "use flint and steel on a creeper in an adventure" (rewards 1 gold-crested chicken)
    private const string CreeperFlintChallengeId = "68337eb8-c1c8-4f57-876a-d262ca7a22f4";
    private const string CreeperFlintReferenceId = "59c9d986-e9a0-4651-9575-9ecb30f932e6";

    // season challenge "collect 6 dark oak slabs" (rewards 1 radius boost III), counts anywhere (mined or crafted on the server)
    private const string DarkOakSlabChallengeId = "3183cf3d-19e5-4bf5-b8a8-3085f4e650d3";
    private const string DarkOakSlabReferenceId = "857ba971-9a35-4119-b674-9cb12bfd0693";

    // season challenge "place 2 rare or higher mobs on a buildplate" (rewards 1 diamond sword)
    private const string PlaceRareMobsChallengeId = "2ebbf878-5c69-4f55-8858-83bd53c57381";
    private const string PlaceRareMobsReferenceId = "f3410363-8eee-4c60-8075-d785c02f158e";

    // season challenge "defeat 3 spiders" (was "bone spiders", which don't exist on the Java server)
    private const string SpiderKillsChallengeId = "a1394f77-20ba-44c2-b46c-e8d7933f2e51";
    private const string SpiderKillsReferenceId = "290e2c67-cdfb-4aab-8fae-f8b910a096c0";

    // season challenge "collect a rare adventure chest" (rewards 5 diamonds)
    private const string RareChestChallengeId = "ab2e0734-62b1-4383-a239-d1b4d4a93dc4";
    private const string RareChestReferenceId = "1d4197fc-49b7-4cca-8397-8792aba78037";

    private static readonly string[] TerracottaColors = ["white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black"];

    // item id of every terracotta color (stained and glazed) -> the event name used for it
    private static readonly Dictionary<string, string> TerracottaItemEvents = new()
    {
        ["8130deac-949a-4016-f7f2-e5dad0df7018"] = "glazed_terracotta_black",
        ["ab5e5e1e-1b73-9ba0-0e6b-2d5051585e69"] = "glazed_terracotta_blue",
        ["595660a4-85b5-ac91-45be-ded273457fe4"] = "glazed_terracotta_brown",
        ["cc92a51e-e8a0-b0b8-d81d-3dbc7f25a8e8"] = "glazed_terracotta_cyan",
        ["f00bc1de-33b7-9420-6638-8c6753f71643"] = "glazed_terracotta_gray",
        ["706e6a56-816f-5b91-d7f9-efbf17455cb9"] = "glazed_terracotta_green",
        ["32932f8e-56cb-9a0d-8c52-6c91b254068a"] = "glazed_terracotta_light_blue",
        ["b6c38658-3dd1-5e14-1ffd-956c4bb4348b"] = "glazed_terracotta_lime",
        ["aa27137d-eb9f-473b-290e-2b8467add1c4"] = "glazed_terracotta_magenta",
        ["7411f28b-5ea9-ce1e-46d4-e1f31ec4b0fe"] = "glazed_terracotta_orange",
        ["96c15281-3347-1b4e-b464-4613d4047bba"] = "glazed_terracotta_pink",
        ["6116b348-0b54-d20f-ac3a-09d285ea5dc5"] = "glazed_terracotta_purple",
        ["e6e669cd-4fb6-dd0f-8eee-1644b53c3eaf"] = "glazed_terracotta_red",
        ["c0589660-aa65-740c-f0c9-08349929460a"] = "glazed_terracotta_light_gray",
        ["a06bf8c7-f350-1a55-ee58-f50dac614b34"] = "terracotta_white",
        ["8fc68864-b1fa-eb75-662a-6f698c642dfc"] = "terracotta_purple",
        ["14a85d17-620d-907c-e76e-303c0ed719be"] = "terracotta_blue",
        ["a8a7d0e5-e606-648d-32cc-4387faa63f70"] = "terracotta_brown",
        ["5f0ada46-8ea4-d3da-efde-bacee07f98c7"] = "terracotta_green",
        ["c55740e4-2c67-ddd1-a9bc-ee4aef35602c"] = "terracotta_red",
        ["65e4db5b-ec63-0fa1-318b-ae1c5e604c22"] = "terracotta_black",
        ["13a8341a-ef9e-ecdb-5400-27eaddd85c37"] = "terracotta_orange",
        ["b1ec391b-2f3c-dd99-cfff-4bee68b01146"] = "terracotta_magenta",
        ["881dc57f-051e-e292-7b18-59a6bf9c1107"] = "terracotta_light_blue",
        ["1c237665-1c0f-6216-c25f-6ebaa7a52af6"] = "terracotta_yellow",
        ["2ac19db3-1394-f4a2-6d47-8102ff4b73e9"] = "terracotta_lime",
        ["6040742d-0d46-8fd9-062c-b36470ba2030"] = "terracotta_pink",
        ["a0b5b0cb-a71f-2889-c3f4-e4558ffaf30c"] = "terracotta_gray",
        ["bd180107-9041-75ce-54ee-4489bd84d07f"] = "terracotta_light_gray",
        ["5d64119b-e9ae-6666-62d6-c6772ea41531"] = "terracotta_cyan",
        ["e7c7f2ff-4233-0824-5d87-3344c9082616"] = "glazed_terracotta_white",
        ["33ed746f-7115-d1b5-1a47-056422800c5d"] = "glazed_terracotta_yellow",
    };

    private static readonly string[] TreeTypes = ["oak", "spruce", "birch", "jungle", "acacia", "dark_oak"];

    /// <summary>
    /// How a named event from the Minecraft server counts for a season challenge.
    /// Unique: every distinct event name only counts once per player (for "different types of ..." challenges).
    /// RequiresOwnBuildplate: only counts while the player is on their own buildplate.
    /// RequiresAdventure: only counts while the player is in an adventure (as the owner or as a guest).
    /// </summary>
    internal sealed record EventRule(string ChallengeId, string ReferenceId, int Threshold, bool Unique, bool RequiresOwnBuildplate, bool RequiresAdventure = false);

    /// <summary>
    /// The table that decides what the "[apace-event] name" lines printed by the datapack count for.
    /// To support another challenge, add a row here and print the matching line from the datapack.
    /// </summary>
    internal static EventRule? GetEventRule(string eventName)
    {
        if (eventName == "spider_sword")
        {
            return new EventRule(SpiderChallengeId, SpiderReferenceId, 2, false, false);
        }

        if (eventName == "adventure_rare_chest")
        {
            return new EventRule(RareChestChallengeId, RareChestReferenceId, 1, false, false, true);
        }

        if (eventName == "dark_oak_slab")
        {
            return new EventRule(DarkOakSlabChallengeId, DarkOakSlabReferenceId, 6, false, false);
        }

        if (eventName == "placed_rare")
        {
            return new EventRule(PlaceRareMobsChallengeId, PlaceRareMobsReferenceId, 2, false, true);
        }

        if (eventName == "spider_kill")
        {
            return new EventRule(SpiderKillsChallengeId, SpiderKillsReferenceId, 3, false, false);
        }

        if (eventName == "collect_salmon")
        {
            return new EventRule(SalmonChallengeId, SalmonReferenceId, 2, false, true);
        }

        if (eventName == "creeper_flint")
        {
            return new EventRule(CreeperFlintChallengeId, CreeperFlintReferenceId, 1, true, false, true);
        }

        if (eventName == "hostile_tnt")
        {
            return new EventRule(HostileTntChallengeId, HostileTntReferenceId, 7, false, false);
        }

        if (eventName is "placed_squid" or "placed_glow_squid")
        {
            return new EventRule(PlaceSquidsChallengeId, PlaceSquidsReferenceId, 2, true, true);
        }

        if (eventName == "gold_ore")
        {
            return new EventRule(GoldOreChallengeId, GoldOreReferenceId, 8, false, false, true);
        }

        if (eventName is "placed_pale_pig" or "placed_parrot" or "placed_salmon")
        {
            return new EventRule(PlaceThreeMobsChallengeId, PlaceThreeMobsReferenceId, 3, true, true);
        }

        if ((eventName.StartsWith("terracotta_", StringComparison.Ordinal) && TerracottaColors.Contains(eventName["terracotta_".Length..])) ||
            (eventName.StartsWith("glazed_terracotta_", StringComparison.Ordinal) && TerracottaColors.Contains(eventName["glazed_terracotta_".Length..])))
        {
            return new EventRule(TerracottaChallengeId, TerracottaReferenceId, 3, true, false);
        }

        if (eventName == "hostile_gold_pickaxe")
        {
            return new EventRule(HostileGoldPickaxeChallengeId, HostileGoldPickaxeReferenceId, 4, false, false);
        }

        if (eventName == "oak_log")
        {
            return new EventRule(OakLogsChallengeId, OakLogsReferenceId, 8, false, false, true);
        }

        if (eventName == "placed_uncommon")
        {
            return new EventRule(PlaceUncommonMobChallengeId, PlaceUncommonMobReferenceId, 1, true, true);
        }

        if (eventName.StartsWith("sapling_", StringComparison.Ordinal) && TreeTypes.Contains(eventName["sapling_".Length..]))
        {
            return new EventRule(SaplingsChallengeId, SaplingsReferenceId, 2, true, true);
        }

        if (eventName.StartsWith("leaves_", StringComparison.Ordinal) && TreeTypes.Contains(eventName["leaves_".Length..]))
        {
            return new EventRule(LeavesChallengeId, LeavesReferenceId, 2, true, false);
        }

        return null;
    }

    /// <summary>
    /// Counts one occurrence of a named event for a player.
    /// Returns true when the event is now (or already was) counted, false when something went wrong.
    /// </summary>
    internal static async Task<bool> RecordChallengeEventAsync(string playerId, string eventName, EventRule rule, CancellationToken cancellationToken = default)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string flagKey = $"event:{eventName}";

            await new EarthDB.Query(true)
                .Get("challenges", playerId, typeof(ChallengeProgressVersion))
                .Get("tokens", playerId, typeof(Tokens))
                .Then(queryResults =>
                {
                    ChallengeProgressVersion progress = queryResults.Get<ChallengeProgressVersion>("challenges");
                    Tokens tokens = queryResults.Get<Tokens>("tokens");

                    progress.EnsureDate(now);
                    progress.SeasonObjectiveCounts ??= [];

                    if (rule.Unique)
                    {
                        if (progress.SeasonObjectiveCounts.ContainsKey(flagKey))
                        {
                            // already counted for this player, nothing to write
                            return new EarthDB.Query(false);
                        }

                        progress.SeasonObjectiveCounts[flagKey] = 1;
                    }

                    CountSeasonProgress(progress, tokens, now, rule.ChallengeId, rule.ReferenceId, rule.Threshold);

                    var query = new EarthDB.Query(true);
                    query.Update("challenges", playerId, progress);
                    query.Update("tokens", playerId, tokens);
                    return query;
                })
                .ExecuteAsync(Program.DB, cancellationToken);

            Log.Information($"[challenges] counted {eventName} for player {playerId}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[challenges] failed to count {eventName} for player {playerId}: {ex}");
            return false;
        }
    }

    /// <summary>
    /// The event name of a terracotta item (one per color), or null when the item is not terracotta.
    /// </summary>
    internal static string? GetTerracottaEventName(string itemId)
        => TerracottaItemEvents.GetValueOrDefault(itemId);

    /// <summary>
    /// Counts a "different types of ..." event for a player directly on their progress, once per event name. Uses the same flag as
    /// <see cref="RecordChallengeEventAsync"/>, so the same color is never counted twice, whichever way it was collected.
    /// This does not look at the player's journal, so a journal that already has every color does not matter.
    /// </summary>
    internal static void CountUniqueSeasonEvent(ChallengeProgressVersion progress, long now, string eventName, string referenceId)
    {
        progress.SeasonObjectiveCounts ??= [];
        string flagKey = $"event:{eventName}";
        if (progress.SeasonObjectiveCounts.ContainsKey(flagKey))
        {
            return;
        }

        progress.SeasonObjectiveCounts[flagKey] = 1;
        progress.AddSeasonObjectiveProgress(now, referenceId);
    }

    internal static async Task RecordAdventureCompletedAsync(string playerId, CancellationToken cancellationToken = default)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            await new EarthDB.Query(true)
                .Get("challenges", playerId, typeof(ChallengeProgressVersion))
                .Get("tokens", playerId, typeof(Tokens))
                .Then(queryResults =>
                {
                    ChallengeProgressVersion progress = queryResults.Get<ChallengeProgressVersion>("challenges");
                    Tokens tokens = queryResults.Get<Tokens>("tokens");

                    progress.EnsureDate(now);
                    CountSeasonProgress(progress, tokens, now, AdventuresChallengeId, AdventuresReferenceId, AdventuresThreshold);

                    var query = new EarthDB.Query(true);
                    query.Update("challenges", playerId, progress);
                    query.Update("tokens", playerId, tokens);
                    return query;
                })
                .ExecuteAsync(Program.DB, cancellationToken);

            Log.Information($"[challenges] counted a completed adventure for player {playerId}");
        }
        catch (Exception ex)
        {
            Log.Error($"[challenges] failed to count a completed adventure for player {playerId}: {ex}");
        }
    }

    /// <summary>
    /// Counts a chicken or a cow placed on the player's buildplate, each animal only once per player.
    /// Returns true when the animal is now (or already was) counted, false when something went wrong.
    /// </summary>
    internal static async Task<bool> RecordMobPlacedAsync(string playerId, string mob, CancellationToken cancellationToken = default)
    {
        if (mob is not "chicken" and not "cow")
        {
            return true;
        }

        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string flagKey = $"placed:{mob}";

            await new EarthDB.Query(true)
                .Get("challenges", playerId, typeof(ChallengeProgressVersion))
                .Get("tokens", playerId, typeof(Tokens))
                .Then(queryResults =>
                {
                    ChallengeProgressVersion progress = queryResults.Get<ChallengeProgressVersion>("challenges");
                    Tokens tokens = queryResults.Get<Tokens>("tokens");

                    progress.EnsureDate(now);
                    progress.SeasonObjectiveCounts ??= [];

                    if (progress.SeasonObjectiveCounts.ContainsKey(flagKey))
                    {
                        // this animal was already counted for this player, nothing to write
                        return new EarthDB.Query(false);
                    }

                    progress.SeasonObjectiveCounts[flagKey] = 1;
                    CountSeasonProgress(progress, tokens, now, PlaceAnimalsChallengeId, PlaceAnimalsReferenceId, PlaceAnimalsThreshold);

                    var query = new EarthDB.Query(true);
                    query.Update("challenges", playerId, progress);
                    query.Update("tokens", playerId, tokens);
                    return query;
                })
                .ExecuteAsync(Program.DB, cancellationToken);

            Log.Information($"[challenges] checked the {mob} placed by player {playerId}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[challenges] failed to count the {mob} placed by player {playerId}: {ex}");
            return false;
        }
    }

    private static void CountSeasonProgress(ChallengeProgressVersion progress, Tokens tokens, long now, string challengeId, string referenceId, int threshold)
    {
        progress.ClaimedChallengeIds ??= [];

        int before = Math.Min(progress.GetObjectiveProgress(referenceId), threshold);
        progress.AddSeasonObjectiveProgress(now, referenceId);
        int after = Math.Min(progress.GetObjectiveProgress(referenceId), threshold);

        // same in-app notification the tappables give for season challenges (progress, then completed)
        if (after > before && !progress.ClaimedChallengeIds.Contains(challengeId))
        {
            Tokens.Token token = after >= threshold
                ? new Tokens.ChallengeCompletedToken(challengeId, referenceId)
                : new Tokens.ChallengeProgressToken(challengeId, referenceId);
            tokens.AddToken(U.RandomUuid().ToString(), token);
        }

        // "Super prizes unlocked" completes by itself together with the mid-season checkpoint
        if (challengeId == HostileGoldPickaxeChallengeId && after >= threshold && progress.GetObjectiveProgress(SuperPrizesReferenceId) < 1)
        {
            progress.AddSeasonObjectiveProgress(now, SuperPrizesReferenceId);
            if (!progress.ClaimedChallengeIds.Contains(SuperPrizesChallengeId))
            {
                tokens.AddToken(U.RandomUuid().ToString(), new Tokens.ChallengeCompletedToken(SuperPrizesChallengeId, SuperPrizesReferenceId));
            }
        }
    }
}
