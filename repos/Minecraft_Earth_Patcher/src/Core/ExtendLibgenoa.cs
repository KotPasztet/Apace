using Serilog;

namespace MCEPatcher.Core;

public static class ExtendLibgenoa
{
    public const string Name = "extend-libgenoa";

    public static void Extent(DirectoryInfo decodedFiles)
    {
        string path = Path.Combine(decodedFiles.FullName, "lib", "arm64-v8a", "libgenoa.so");
        string target = path + ".hexdump";

        if (File.Exists(target))
        {
            return;
        }

        byte[] oldBytes = File.ReadAllBytes(path);

        byte[] newBytes = new byte[oldBytes.Length + 10965232];

        Array.ConstrainedCopy(oldBytes, 0, newBytes, 0, oldBytes.Length);
        Array.Fill(newBytes, (byte)0xFF, 0x07000000, newBytes.Length - 0x07000000);

        ((ReadOnlySpan<byte>)[0x00, 0x20, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00])
            .CopyTo(newBytes.AsSpan(0x60));

        Log.Information($"Creating hexdump for '{path}'");
        File.WriteAllText(target, HexDump.Create(newBytes));
    }
}
