using System.Collections.Concurrent;

namespace Apace.ApiServer.Utils;

// Simple per-key sliding window rate limiter, intended for low-volume
// authenticated endpoints (e.g. multiplayer join attempts). This is good
// enough to stop spam and brute force attempts; it is not a general purpose
// QoS limiter.
internal static class RateLimiter
{
    private sealed class Window
    {
        public readonly Queue<long> Timestamps = new();
    }

    private const int MaxTrackedKeys = 4096;

    private static readonly ConcurrentDictionary<string, Window> _windows = [];

    // Returns true if the event is allowed and records it, false if the key
    // has already hit the limit.
    public static bool IsAllowed(string key, int maxEvents, TimeSpan window)
    {
        long windowMs = (long) window.TotalMilliseconds;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Window windowObj = _windows.GetOrAdd(key, _ => new Window());

        if (_windows.Count > MaxTrackedKeys)
        {
            SweepStale(windowMs);
        }

        lock (windowObj.Timestamps)
        {
            while (windowObj.Timestamps.Count > 0 && windowObj.Timestamps.Peek() < now - windowMs)
            {
                windowObj.Timestamps.Dequeue();
            }

            if (windowObj.Timestamps.Count >= maxEvents)
            {
                return false;
            }

            windowObj.Timestamps.Enqueue(now);
            return true;
        }
    }

    // Remove windows that have gone fully stale so the dictionary cannot grow
    // without bound. Called rarely, only once the key count goes over the cap.
    private static void SweepStale(long windowMs)
    {
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - windowMs;

        foreach (KeyValuePair<string, Window> entry in _windows)
        {
            lock (entry.Value.Timestamps)
            {
                if (entry.Value.Timestamps.Count == 0 || entry.Value.Timestamps.Peek() < cutoff)
                {
                    _windows.TryRemove(entry.Key, out _);
                }
            }
        }
    }
}
