using Apace.DB.Models.Common;
using Apace.DB.Models.Player;

namespace Apace.ApiServer.Utils;

internal sealed class AdventureBackpacks
{
    private readonly Dictionary<(string InstanceId, string PlayerId), Backpack> _backpacks = [];

    public void Open(string instanceId, string playerId, Hotbar hotbar)
    {
        var backpack = new Backpack();
        foreach (Hotbar.Item? item in hotbar.Items)
        {
            if (item is not null && item.Count > 0)
            {
                backpack.Add(item.Uuid, item.Count, item.InstanceId);
            }
        }

        lock (_backpacks)
        {
            _backpacks.TryAdd((instanceId, playerId), backpack);
        }
    }

    public void Close(string instanceId, string playerId)
    {
        lock (_backpacks)
        {
            _backpacks.Remove((instanceId, playerId));
        }
    }

    public void Add(string instanceId, string playerId, string itemId, int count, string? itemInstanceId)
    {
        lock (_backpacks)
        {
            if (_backpacks.TryGetValue((instanceId, playerId), out Backpack? backpack))
            {
                backpack.Add(itemId, count, itemInstanceId);
            }
        }
    }

    public void Remove(string instanceId, string playerId, string itemId, int count, string? itemInstanceId)
    {
        lock (_backpacks)
        {
            if (_backpacks.TryGetValue((instanceId, playerId), out Backpack? backpack))
            {
                backpack.Remove(itemId, count, itemInstanceId);
            }
        }
    }

    public Inventory? GetContents(string instanceId, string playerId, Inventory inventory)
    {
        lock (_backpacks)
        {
            return _backpacks.TryGetValue((instanceId, playerId), out Backpack? backpack) ? backpack.LimitTo(inventory) : null;
        }
    }

    private sealed class Backpack
    {
        private readonly Dictionary<string, int> _counts = [];
        private readonly Dictionary<string, HashSet<string>> _instances = [];

        public void Add(string itemId, int count, string? itemInstanceId)
        {
            if (itemInstanceId is not null)
            {
                if (!_instances.TryGetValue(itemId, out HashSet<string>? instances))
                {
                    instances = [];
                    _instances[itemId] = instances;
                }

                instances.Add(itemInstanceId);
            }
            else if (count > 0)
            {
                _counts[itemId] = _counts.GetValueOrDefault(itemId) + count;
            }
        }

        public void Remove(string itemId, int count, string? itemInstanceId)
        {
            if (itemInstanceId is not null)
            {
                if (_instances.TryGetValue(itemId, out HashSet<string>? instances))
                {
                    instances.Remove(itemInstanceId);
                }
            }
            else if (_counts.TryGetValue(itemId, out int current))
            {
                _counts[itemId] = Math.Max(0, current - count);
            }
        }

        public Inventory LimitTo(Inventory inventory)
        {
            var contents = new Inventory();
            foreach (var (itemId, count) in _counts)
            {
                int owned = Math.Min(count, inventory.GetItemCount(itemId));
                if (owned > 0)
                {
                    contents.AddItems(itemId, owned);
                }
            }

            foreach (var (itemId, instanceIds) in _instances)
            {
                NonStackableItemInstance[] owned = [.. inventory.GetItemInstances(itemId).Where(instance => instanceIds.Contains(instance.InstanceId))];
                if (owned.Length > 0)
                {
                    contents.AddItems(itemId, owned);
                }
            }

            return contents;
        }
    }
}
