using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Serilog;
using Solace.Common;
using Solace.EventBus.Client;

namespace Solace.ApiServer.Utils;

internal static class TileUtils
{
    private static EventBusClient eventBus => Program.eventBus;
    private static readonly byte[] EmptyTilePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAABOklEQVR4nO3SMQ0AAAwCoNm/9HI83BLIOQmtnpnZB4CjEwABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEoAB1XQB3P+pKnEAAAAASUVORK5CYII=");

    private static RequestSender? _requestSender;
    private static readonly SemaphoreSlim _requestSenderLock = new(1, 1);

    // In-memory cache of rendered tiles. Map data comes from a read-only OSM planet
    // database (or the external Maptiler API), so rendered tiles are immutable and are
    // cached without expiry. Only fallback (render failure) entries expire, so a tile
    // that failed to render while the renderer was unavailable is retried later.
    private static readonly ConcurrentDictionary<long, CachedTile> TileCache = new();
    private static long _cacheClock;
    private const int MaxCachedTiles = 512;
    private static readonly TimeSpan FallbackTileTtl = TimeSpan.FromMinutes(10);

    public readonly record struct CachedTileHit(string ETag, byte[] Png);

    private sealed class CachedTile
    {
        public required string ETag { get; init; }
        public required byte[] Png { get; init; }
        public required bool IsFallback { get; init; }
        public required DateTimeOffset StoredAt { get; init; }
        public long LastUsed;
    }

    /// <summary>
    /// Gets a tile from the in-memory cache without any I/O (no event bus, no renderer).
    /// Returns null when the tile has not been rendered yet (or its fallback entry expired).
    /// </summary>
    public static CachedTileHit? LookupCachedTile(int tileX, int tileY)
    {
        long key = ToCacheKey(tileX, tileY);

        if (!TileCache.TryGetValue(key, out CachedTile? cached) || IsExpired(cached))
        {
            TileCache.TryRemove(key, out _);
            return null;
        }

        Touch(cached);
        return new CachedTileHit(cached.ETag, cached.Png);
    }

    /// <summary>
    /// Gets a tile PNG and its ETag, rendering it over the event bus on a cache miss.
    /// Both rendered tiles and fallback (empty) tiles end up in the cache.
    /// </summary>
    public static async Task<(string ETag, byte[] Png)> GetTileAsync(int tileX, int tileY, CancellationToken cancellationToken)
    {
        CachedTileHit? hit = LookupCachedTile(tileX, tileY);
        if (hit is not null)
        {
            return (hit.Value.ETag, hit.Value.Png);
        }

        byte[]? tilePng = await TryRenderTile(tileX, tileY, cancellationToken);
        bool isFallback = tilePng is null;
        if (isFallback)
        {
            Log.Warning("Serving fallback tile {TileX},{TileY}", tileX, tileY);
            tilePng = EmptyTilePng;
        }

        var entry = new CachedTile
        {
            ETag = ComputeETag(tilePng),
            Png = tilePng,
            IsFallback = isFallback,
            StoredAt = DateTimeOffset.UtcNow,
            LastUsed = Interlocked.Increment(ref _cacheClock),
        };

        TileCache[ToCacheKey(tileX, tileY)] = entry;
        TrimCache();

        return (entry.ETag, entry.Png);
    }

    private static bool IsExpired(CachedTile cached)
        => cached.IsFallback && DateTimeOffset.UtcNow - cached.StoredAt > FallbackTileTtl;

    private static void Touch(CachedTile cached)
        => cached.LastUsed = Interlocked.Increment(ref _cacheClock);

    private static void TrimCache()
    {
        int excess = TileCache.Count - MaxCachedTiles;
        if (excess <= 0)
        {
            return;
        }

        foreach (var victim in TileCache.OrderBy(static entry => entry.Value.LastUsed).Take(excess))
        {
            TileCache.TryRemove(victim.Key, out _);
        }
    }

    private static string ComputeETag(byte[] png)
    {
#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms - ok for etag
        byte[] hash = SHA1.HashData(png);
#pragma warning restore CA5350
        return $"\"{WebEncoders.Base64UrlEncode(hash)}\"";
    }

    private static async Task<byte[]?> TryRenderTile(int tileX, int tileY, CancellationToken cancellationToken)
    {
        string? response;

        await _requestSenderLock.WaitAsync(cancellationToken);
        try
        {
            _requestSender ??= await eventBus.AddRequestSenderAsync();

            Task<string?> responseTask = _requestSender.RequestAsync("tile", "renderTile", Json.Serialize(new RenderTileRequest(tileX, tileY, 16)));
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
            if (await Task.WhenAny(responseTask, timeoutTask) != responseTask)
            {
                Log.Warning("Tile render timed out for tile {TileX},{TileY}", tileX, tileY);
                await ResetRequestSenderAsync();
                return null;
            }

            response = await responseTask;
        }
        catch (Exception ex) when (ex is EventBusClientException or InvalidOperationException)
        {
            Log.Warning(ex, "Tile render request failed for tile {TileX},{TileY}", tileX, tileY);
            await ResetRequestSenderAsync();
            return null;
        }
        finally
        {
            _requestSenderLock.Release();
        }

        if (string.IsNullOrWhiteSpace(response))
        {
            Log.Warning("Tile renderer returned no data for tile {TileX},{TileY}", tileX, tileY);
            return null;
        }

        try
        {
            return Convert.FromBase64String(response);
        }
        catch (FormatException ex)
        {
            Log.Warning(ex, "Tile renderer returned invalid base64 for tile {TileX},{TileY}", tileX, tileY);
            return null;
        }
    }

    private static async Task ResetRequestSenderAsync()
    {
        if (_requestSender is not null)
        {
            try
            {
                await _requestSender.CloseAsync();
            }
            catch
            {
                // The connection is already broken; the next request will create a new sender.
            }
        }

        _requestSender = null;
    }

    private static long ToCacheKey(int tileX, int tileY)
        => unchecked((long)tileX | ((long)tileY << 32));

    private sealed record RenderTileRequest(int TileX, int TileY, int Zoom);
}
