namespace Apace.ApiServer.Utils;

// Caches the (expensive to build) catalog API responses in memory for a
// limited time, so that the large LINQ mappings over the item catalog are
// not redone for every client request. Static state mirrors how the rest of
// the API server holds its singletons (see Program.staticData).
internal static class CatalogResponseCache<T> where T : class
{
    private sealed record Entry(
        DateTimeOffset ExpiresAt,
        Lazy<T> Value
    );

    private static readonly object _lock = new();
    private static Entry? _entry;

    public static T GetOrCreate(Func<T> factory, TimeSpan ttl)
    {
        lock (_lock)
        {
            if (_entry is not null && _entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return _entry.Value.Value;
            }

            Lazy<T> lazy = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);
            _entry = new Entry(DateTimeOffset.UtcNow.Add(ttl), lazy);
            return lazy.Value;
        }
    }
}
