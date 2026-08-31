using Serilog;
using SharpNBT;
using System.IO.Compression;
using Solace.Common.Utils;

namespace Solace.PreviewGenerator;

internal sealed class ServerDataZip
{
    public static ServerDataZip Read(Stream inputStream)
        => new ServerDataZip(inputStream);

    private readonly Dictionary<string, byte[]> _files = [];

    private ServerDataZip(Stream inputStream)
    {
        using var archive = new ZipArchive(inputStream);

        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            using (Stream entryStream = entry.Open())
            using (var ms = new MemoryStream())
            {
                entryStream.CopyTo(ms);
                _files.Add(entry.FullName, ms.ToArray());
            }
        }
    }

    /// <summary>
    /// Reads the NBT of a chunk, or returns <c>null</c> for chunks that are not present in the server data (missing
    /// region file, never saved chunk or unreadable/corrupt chunk data). A <c>null</c> chunk should be rendered as air.
    /// </summary>
    public CompoundTag? GetChunkNBT(int x, int z)
    {
        int regionX = x >> 5;
        int regionZ = z >> 5;
        int chunkX = x & 31;
        int chunkZ = z & 31;
        int chunkIndex = (chunkZ << 5) | chunkX;

        // buildplate exports only contain region files that exist on disk, a missing region file means the whole chunk was never generated
        if (!_files.TryGetValue($"region/r.{regionX}.{regionZ}.mca", out byte[]? regionData))
        {
            return null;
        }

        try
        {
            using var ms = new MemoryStream(regionData);
            using var reader = new BinaryReader(ms);

            // the location and timestamp tables occupy the first two sectors of the file
            if (ms.Length < 2 * 4096)
            {
                return null;
            }

            ms.Seek(chunkIndex * 4, SeekOrigin.Begin);
            int offset = (int)(reader.ReadUInt32BE() >> 8);

            // the first two sectors are the header, an offset of zero means the chunk was never saved
            if (offset < 2)
            {
                return null;
            }

            long chunkDataOffset = offset * 4096L;

            // the chunk data length field and compression type byte must fit in the file,
            // length includes the compression type byte and the payload must fit in the file
            if (chunkDataOffset + 5 > ms.Length)
            {
                return null;
            }

            ms.Seek(chunkDataOffset, SeekOrigin.Begin);

            int length = (int)reader.ReadUInt32BE();
            if (length < 2 || chunkDataOffset + 4 + length > ms.Length)
            {
                return null;
            }

            byte compressionType = reader.ReadByte();
            byte[] compressed = new byte[length];
            ms.Read(compressed);
            byte[] uncompressed;
            switch (compressionType)
            {
                case 0: // uncompressed (nonstandard)
                case 3:
                    {
                        uncompressed = compressed;
                        break;
                    }
                case 1:
                    {
                        using var gZipStream = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress, false);
                        using var resultStream = new MemoryStream();
                        gZipStream.CopyTo(resultStream);
                        uncompressed = resultStream.ToArray();
                    }

                    break;
                case 2:
                    {
                        using var deflateStream = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress, false);
                        using var resultStream = new MemoryStream();
                        deflateStream.CopyTo(resultStream);
                        uncompressed = resultStream.ToArray();
                    }

                    break;
                default:
                    Log.Debug($"Ignoring chunk {x}, {z} in region r.{regionX}.{regionZ}.mca with invalid compression type {compressionType}");
                    return null;
            }

            using (var tagStream = new MemoryStream(uncompressed))
            using (var tagReader = new TagReader(tagStream, FormatOptions.Java, false))
            {
                CompoundTag tag = tagReader.ReadTag<CompoundTag>();

                return tag;
            }
        }
        catch (Exception ex)
        {
            // truncated or corrupt region data, render the chunk as air instead of failing the whole preview
            Log.Debug($"Could not read chunk {x}, {z} from region r.{regionX}.{regionZ}.mca: {ex.Message}");
            return null;
        }
    }
}
