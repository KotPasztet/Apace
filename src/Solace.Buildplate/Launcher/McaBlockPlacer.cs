using System.IO.Compression;

namespace Solace.Buildplate.Launcher;

/// <summary>
/// Parses Minecraft .mca region files and places blocks via RCON /setblock.
/// Workaround for runtime chunk loading limitations.
/// </summary>
public static class McaBlockPlacer
{
    /// <summary>
    /// Parse all .mca files in the given directory and place non-air blocks
    /// at the specified offset via RCON /setblock commands.
    /// Limit to 500 blocks for performance.
    /// </summary>
    public static async Task<int> PlaceAsync(string mcaDir, MinecraftRconClient rcon, int offsetX, int offsetZ)
    {
        if (!Directory.Exists(mcaDir)) return 0;
        int count = 0;
        foreach (var mca in Directory.GetFiles(mcaDir, "r.*.*.mca"))
        {
            var name = Path.GetFileNameWithoutExtension(mca);
            var parts = name.Split('.');
            if (parts.Length < 3 || !int.TryParse(parts[1], out int rx) || !int.TryParse(parts[2], out int rz))
                continue;
            count += await PlaceRegionAsync(mca, rcon, rx + offsetX / 512, rz + offsetZ / 512, int.MaxValue);
        }
        return count;
    }

    private static async Task<int> PlaceRegionAsync(string path, MinecraftRconClient rcon, int regionX, int regionZ, int remaining)
    {
        var data = await File.ReadAllBytesAsync(path);
        int count = 0;
        int chunksAttempted = 0, chunksSkipped = 0;

        // Dump first 20 header entries for debug
        var headerInfo = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            int off = data[i*4] << 16 | data[i*4+1] << 8 | data[i*4+2];
            int sec = data[i*4+3];
            if (off != 0) headerInfo.Add($"#{i}:{off}/{sec}");
        }
        Serilog.Log.Information("McaHeader [{File}]: {Info}", Path.GetFileName(path),
            headerInfo.Count > 0 ? string.Join(" ", headerInfo) : "ALL EMPTY");

        for (int cz = 0; cz < 32 && count < remaining; cz++)
        {
            for (int cx = 0; cx < 32 && count < remaining; cx++)
            {
                int entry = (cx + cz * 32) * 4;
                int offset = data[entry] << 16 | data[entry + 1] << 8 | data[entry + 2];
                byte sectors = data[entry + 3];
                if (offset == 0 || sectors == 0) continue;
                
                int cs = offset * 4096;
                byte[] lenBytes = { data[cs + 3], data[cs + 2], data[cs + 1], data[cs] };
                if (!BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                int length = BitConverter.ToInt32(lenBytes, 0);
                if (length <= 1 || length > sectors * 4096) { chunksSkipped++; continue; }

                byte compType = data[cs + 4];
                byte[] chunkData = new byte[length - 1];
                Array.Copy(data, cs + 5, chunkData, 0, length - 1);

                chunksAttempted++;
                using var ms = compType switch
                {
                    2 => (Stream)new ZLibStream(new MemoryStream(chunkData), CompressionMode.Decompress),
                    _ => new GZipStream(new MemoryStream(chunkData), CompressionMode.Decompress)
                };
                using var reader = new BinaryReader(ms);

                count += await PlaceChunkBlocks(reader, rcon, regionX * 32 + cx, regionZ * 32 + cz, remaining - count);
            }
        }
        Serilog.Log.Information("McaBlock: {Path} — {Attempted} chunks, {Skipped} skipped, {Count} blocks",
            Path.GetFileName(path), chunksAttempted, chunksSkipped, count);
        return count;
    }

    private static async Task<int> PlaceChunkBlocks(BinaryReader r, MinecraftRconClient rcon, int cx, int cz, int max)
    {
        int count = 0;
        byte tagType = r.ReadByte();
        if (tagType != 10) return 0; // not TAG_Compound
        r.ReadBytes(2); // root name length (empty = 00 00)
        
        // Find sections list
        if (!FindTag(r, 9, "sections")) return 0;
        r.ReadByte(); // list element type
        int sectionCount = ReadIntBE(r);

        for (int si = 0; si < sectionCount && count < max; si++)
        {
            r.ReadByte(); // TAG_Compound for each section
            r.ReadBytes(2); // skip name (empty)
            
            // Find Y
            if (!FindTag(r, 1, "Y")) continue;
            int sy = r.ReadByte() & 0xFF;
            
            // Find block_states
            if (!FindTag(r, 10, "block_states")) continue;
            r.ReadBytes(2); // skip name
            
            // Read palette
            if (!FindTag(r, 9, "palette")) continue;
            r.ReadByte(); // element type
            int psize = ReadIntBE(r);
            var palette = new string[psize];
            for (int pi = 0; pi < psize; pi++)
            {
                r.ReadByte(); // TAG_Compound
                r.ReadBytes(2); // skip name
                if (FindTag(r, 8, "Name"))
                {
                    r.ReadBytes(2); // name length
                    int nl = (r.ReadByte() << 8) | r.ReadByte();
                    palette[pi] = new string(r.ReadChars(nl));
                }
                else palette[pi] = "minecraft:air";
            }
            
            // Read block data
            if (!FindTag(r, 12, "data")) continue; // TAG_Long_Array
            r.ReadBytes(2);
            int dataLen = ReadIntBE(r);
            var bd = new long[dataLen];
            for (int di = 0; di < dataLen; di++)
                bd[di] = ReadInt64BE(r);

            if (psize > 0)
            {
                int bits = Math.Max(4, (int)Math.Ceiling(Math.Log2(psize)));
                for (int y = 0; y < 16 && count < max; y++)
                for (int z = 0; z < 16 && count < max; z++)
                for (int x = 0; x < 16 && count < max; x++)
                {
                    int idx = y * 256 + z * 16 + x;
                    int pi = GetBits(bd, idx * bits, bits);
                    if (pi < 0 || pi >= psize) continue;
                    var block = palette[pi];
                    if (block == "minecraft:air" || block == "minecraft:cave_air" || block == "minecraft:void_air")
                        continue;
                    
                    int wx = cx * 16 + x, wy = sy * 16 + y, wz = cz * 16 + z;
                    await rcon.SendCommandAsync($"setblock {wx} {wy} {wz} {block}");
                    count++;
                }
            }
        }
        return count;
    }

    private static bool FindTag(BinaryReader r, byte expectedType, string expectedName)
    {
        byte t = r.ReadByte();
        if (t != expectedType) return false;
        int nl = (r.ReadByte() << 8) | r.ReadByte();
        var name = new string(r.ReadChars(nl));
        return name == expectedName;
    }

    private static int ReadIntBE(BinaryReader r)
    { var b = r.ReadBytes(4); return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]; }
    private static long ReadInt64BE(BinaryReader r)
    { var b = r.ReadBytes(8); return ((long)b[0] << 56) | ((long)b[1] << 48) | ((long)b[2] << 40) | ((long)b[3] << 32) | ((long)b[4] << 24) | ((long)b[5] << 16) | ((long)b[6] << 8) | b[7]; }
    private static int GetBits(long[] data, int start, int bits)
    { int li = start / 64, bi = start % 64; if (li >= data.Length) return 0; long v = (long)((ulong)data[li] >> bi); if (bi + bits > 64 && li + 1 < data.Length) v |= data[li + 1] << (64 - bi); return (int)(v & ((1L << bits) - 1)); }
}
