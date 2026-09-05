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
        "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAABOklEQVR4nO3SMQ0AAAwCoNm/9HI83BLIOQmtnpnZB4CjEwABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEgABEoAB1XQB3P+pKnEAAAAASUVORK5CYII=");

    private static RequestSender? _requestSender;
    private static readonly SemaphoreSlim _requestSenderLock = new(1, 1);

    // Rendered tiles only depend on their coordinates and zoom, and map data never
    // changes, so they are cached without an expiry. Fallback (empty) tiles do expire,
    // so a tile that failed to render while the renderer was down is retried later.
    private const int MaxCachedTiles = 512;
    private static readonly TimeSpan FallbackTileTtl = TimeSpan.FromMinutes(10);
    private static readonly object TileCacheLock = new();
    private static readonly Dictionary<long, LinkedListNode<CachedTile>> TileCache = new();
    private static readonly LinkedList<CachedTile> TileCacheLru = new(); // first = most recently used

    public readonly record struct CachedTileHit(string ETag, byte[] Png);

    private sealed class CachedTile
    {
        public required long Key { get; init; }
        public required string ETag { get; init; }
        public required byte[] Png { get; init; }
        public required bool IsFallback { get; init; }
        public required DateTimeOffset StoredAt { get; init; }

        public bool IsExpired => IsFallback && DateTimeOffset.UtcNow - StoredAt > FallbackTileTtl;
    }

    /// <summary>
    /// Looks a tile up in the in-memory cache without any I/O (no event bus, no renderer).
    /// Returns null when the tile has not been rendered yet (or its fallback entry expired).
    /// </summary>
    public static CachedTileHit? LookupCachedTile(int tileX, int tileY)
    {
        lock (TileCacheLock)
        {
            if (!TileCache.TryGetValue(ToCacheKey(tileX, tileY), out var node) || node.Value.IsExpired)
            {
                if (node is not null)
                {
                    TileCacheLru.Remove(node);
                    TileCache.Remove(node.Value.Key);
                }

                return null;
            }

            TileCacheLru.Remove(node);
            TileCacheLru.AddFirst(node);
            return new CachedTileHit(node.Value.ETag, node.Value.Png);
        }
    }

    /// <summary>
    /// Gets a tile PNG and its ETag, rendering it over the event bus on a cache miss.
    /// Both rendered tiles and fallback (empty) tiles end up in the cache.
    /// </summary>
    public static async Task<(string ETag, byte[] Png)> GetTileAsync(int tileX, int tileY, CancellationToken cancellationToken)
    {
        if (LookupCachedTile(tileX, tileY) is { } cached)
        {
            return (cached.ETag, cached.Png);
        }

        byte[]? tilePng = await TryRenderTile(tileX, tileY, cancellationToken);
        bool isFallback = tilePng is null;
        if (isFallback)
        {
            Log.Warning("Serving fallback tile {TileX},{TileY}", tileX, tileY);
            tilePng = EmptyTilePng;
        }

        var tile = new CachedTile
        {
            Key = ToCacheKey(tileX, tileY),
            ETag = ComputeETag(tilePng),
            Png = tilePng,
            IsFallback = isFallback,
            StoredAt = DateTimeOffset.UtcNow,
        };

        lock (TileCacheLock)
        {
            if (TileCache.TryGetValue(tile.Key, out var existing))
            {
                TileCacheLru.Remove(existing);
            }

            var node = new LinkedListNode<CachedTile>(tile);
            TileCacheLru.AddFirst(node);
            TileCache[tile.Key] = node;

            while (TileCache.Count > MaxCachedTiles && TileCacheLru.Last is { } lru)
            {
                TileCacheLru.RemoveLast();
                TileCache.Remove(lru.Value.Key);
            }
        }

        return (tile.ETag, tile.Png);
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
