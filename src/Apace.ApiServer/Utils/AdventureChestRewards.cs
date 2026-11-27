using Serilog;
using Apace.DB;
using Apace.StaticData;

namespace Apace.ApiServer.Utils;

public static class AdventureChestRewards
{
    private static readonly HashSet<string> raritiesWithoutLoot = [];
    private static readonly Lock raritiesWithoutLootLock = new();

    public static Task<IReadOnlyDictionary<string, int>?> RollAndGiveAsync(string playerId, string instanceId, string rarity, long currentTime)
        => RollAndGiveAsync(
            Program.DB,
            Program.staticData,
            Random.Shared,
            playerId,
            instanceId,
            rarity,
            currentTime);

    internal static async Task<bool> GiveAsync(EarthDB earthDB, StaticData.StaticData staticData, Random random, string playerId, string instanceId, string rarity, long currentTime)
        => await RollAndGiveAsync(earthDB, staticData, random, playerId, instanceId, rarity, currentTime) is not null;

    internal static async Task<IReadOnlyDictionary<string, int>?> RollAndGiveAsync(EarthDB earthDB, StaticData.StaticData staticData, Random random, string playerId, string instanceId, string rarity, long currentTime)
    {
        try
        {
            AdventureLoot.Table? table = staticData.AdventureLoot.GetTable(rarity);
            if (table is null)
            {
                bool firstTime;
                lock (raritiesWithoutLootLock)
                {
                    firstTime = raritiesWithoutLoot.Add(rarity.Trim().ToLowerInvariant());
                }

                if (firstTime)
                {
                    Log.Warning("No adventure loot for rarity {Rarity}, adventure chests of that rarity give nothing", rarity);
                }

                return null;
            }

            Catalog.ItemsCatalogR itemsCatalog = staticData.Catalog.ItemsCatalog;
            Dictionary<string, int> items = [];
            foreach (AdventureLoot.Drop drop in table.RollDrops(random))
            {
                // granting an item that is not in the catalog fails, so it is left out here rather than at redemption
                if (itemsCatalog.GetItem(drop.Item.Id) is null)
                {
                    Log.Warning("Adventure loot item {ItemId} ({Name}) is not in the item catalog", drop.Item.Id, drop.Item.Name);
                    continue;
                }

                items[drop.Item.Id] = items.GetValueOrDefault(drop.Item.Id) + drop.Count;
            }

            if (items.Count == 0)
            {
                Log.Warning("Adventure chest ({Rarity}) for {PlayerId} in instance {InstanceId} gave nothing", rarity, playerId, instanceId);
                return null;
            }

            string description = string.Join(", ", items.Select(item => $"{item.Value}x {Describe(itemsCatalog.GetItem(item.Key)!)}"));
            Log.Information("Adventure chest ({Rarity}) for {PlayerId} in instance {InstanceId}: {Items}", rarity, playerId, instanceId, description);

            EarthDB.Results results = await TokenUtils.AddOrUpdateAdventureRewardToken(playerId, instanceId, items).ExecuteAsync(earthDB);
            Log.Information("Adventure reward token {TokenId} for instance {InstanceId} issued to {PlayerId}", results.GetExtra("tokenId"), instanceId, playerId);

            return items;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Could not give adventure chest loot to {PlayerId} for instance {InstanceId}", playerId, instanceId);
            return null;
        }
    }

    private static string Describe(Catalog.ItemsCatalogR.Item item)
        => item.Aux != 0 ? $"{item.Name}:{item.Aux}" : item.Name;
}
