namespace Apace.ApiServer.Utils;

// a chest is only handled once, even if its break is reported again
internal sealed class BrokenAdventureChests
{
    private readonly long _expiryMillis;
    private readonly Dictionary<(string InstanceId, int X, int Y, int Z), long> _brokenAt = [];

    public BrokenAdventureChests(TimeSpan expiry)
    {
        _expiryMillis = (long)expiry.TotalMilliseconds;
    }

    public bool TryAdd(string instanceId, int x, int y, int z, long currentTime)
    {
        lock (_brokenAt)
        {
            foreach (var entry in _brokenAt.Where(entry => currentTime - entry.Value >= _expiryMillis).ToArray())
            {
                _brokenAt.Remove(entry.Key);
            }

            return _brokenAt.TryAdd((instanceId, x, y, z), currentTime);
        }
    }
}
