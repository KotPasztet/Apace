using Serilog;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MCEPatcher.Core;

public static class IpaProcessor
{
    public static readonly string IpaHash = "91E235E52D430123FB73479512AD8BBE238518729BE9C6A0F372B9AFC876573B";

    internal static bool Autonomous { get; private set; }

    public static bool VerifyHash(Stream stream)
    {
        byte[] hashBytes = SHA256.HashData(stream);
        var hash = Convert.ToHexString(hashBytes);
        return hash == IpaHash;
    }

    public static async Task<bool> Run(Options options)
    {
        Autonomous = options.Autonomous;

        string inIpa = options.InIpa;
        string outIpa = options.OutIpa;
        string decodedDir = options.DecodedDir;

        if (!File.Exists(inIpa))
        {
            Log.Fatal($"In ipa '{inIpa}' doesn't exist");
            return false;
        }

        string authProtocol = options.Protocol == 0 ? "http://" : "https://";
        string authBaseUrl = $"{authProtocol}{options.Hostname}";
        string locatorProtocol = options.LocatorProtocol == 0 ? "http://" : "https://";
        string locatorBaseUrl = $"{locatorProtocol}{options.LocatorHostname}";

        Log.Information("*****Extracting IPA*****");
        if (Directory.Exists(decodedDir))
            Directory.Delete(decodedDir, true);

        ZipFile.ExtractToDirectory(inIpa, decodedDir);
        Log.Debug("Done");

        string payloadDir = Path.Combine(decodedDir, "Payload");
        if (!Directory.Exists(payloadDir))
        {
            Log.Fatal("IPA doesn't contain a Payload directory");
            return false;
        }

        var appDirs = Directory.GetDirectories(payloadDir, "*.app");
        if (appDirs.Length == 0)
        {
            Log.Fatal("No .app bundle found in Payload");
            return false;
        }

        string appDir = appDirs[0];
        string exePath = Path.Combine(appDir, "minecraftearthtf");

        if (!File.Exists(exePath))
        {
            Log.Fatal($"Main executable not found at '{exePath}'");
            return false;
        }

        if (options.ChangeLocatorAddress)
        {
            Log.Information("Patching locator address");
            byte[] searchBytes = Encoding.ASCII.GetBytes("https://locator.mceserv.net");
            byte[] replaceBytes = AsciiToFixedBytes(locatorBaseUrl, 27);
            GlobalReplaceBytes(exePath, searchBytes, replaceBytes);
            Log.Debug("Done");
        }

        if (options.DisableSunsetTimeCheck)
        {
            Log.Information("Disabling sunset time check");
            byte[] sunsetPatch = { 0xE1, 0x05, 0x00, 0x54 };
            PatchAtOffset(exePath, 0x1129080, sunsetPatch);
            Log.Debug("Done");
        }

        if (options.DisableSpeedLimit)
        {
            Log.Information("Disabling moving-too-fast speed limit");
            PatchSpeedLimit(exePath);
            Log.Debug("Done");
        }

        if (options.RemoveDrm)
        {
            Log.Information("Removing DRM and code signature");
            RemoveDrm(appDir);
            Log.Debug("Done");
        }

        if (options.ChangeAppName && !string.IsNullOrWhiteSpace(options.AppName))
        {
            Log.Information($"Changing app name to '{options.AppName}'");
            PatchAppName(appDir, options.AppName);
            Log.Debug("Done");
        }

        if (options.ChangeIcon && !string.IsNullOrWhiteSpace(options.IconPath))
        {
            Log.Information("Changing app icon");
            IconPatcher.PatchIosIcon(appDir, options.IconPath);
            Log.Debug("Done");
        }

        if (options.ChangeXalAuthAddress)
        {
            string xalPath = Path.Combine(appDir, "Frameworks", "Xal.framework", "Xal");
            if (!File.Exists(xalPath))
            {
                Log.Fatal($"Xal framework not found at '{xalPath}'");
                return false;
            }

            string xalScheme = options.XalProtocol.HasValue
                ? (options.XalProtocol.Value == 0 ? "http" : "https")
                : (options.Protocol == 0 ? "http" : "https");
            string xalHostname = string.IsNullOrWhiteSpace(options.XalHostname)
                ? options.Hostname.Trim().ToLowerInvariant()
                : options.XalHostname.Trim().ToLowerInvariant();

            Log.Information("Patching Xal auth address");
            PatchXalAuth(xalPath, xalScheme, xalHostname);
            Log.Debug("Done");
        }

        if (options.ForceInAppWebView)
        {
            string xalPath = Path.Combine(appDir, "Frameworks", "Xal.framework", "Xal");
            if (!File.Exists(xalPath))
            {
                Log.Fatal($"Xal framework not found at '{xalPath}'");
                return false;
            }

            Log.Information("Patching Xal web auth presentation");
            PatchXalWebViewPresentation(xalPath);
            Log.Debug("Done");
        }

        Log.Information("Patching ATS exceptions for redirected endpoints");
        PatchInfoPlistAts(appDir,
            options.Hostname?.Trim()?.ToLowerInvariant(),
            options.LocatorHostname?.Trim()?.ToLowerInvariant(),
            options.XalHostname?.Trim()?.ToLowerInvariant(),
            options.PlayfabApiHostname?.Trim()?.ToLowerInvariant(),
            options.XboxABHostname?.Trim()?.ToLowerInvariant(),
            options.XboxLiveHostname?.Trim()?.ToLowerInvariant());
        Log.Debug("Done");

        if (options.ChangePlayfabApiAddress || options.ChangeXboxABAddress || options.ChangeXboxLiveAddress)
        {
            Log.Information("Patching service endpoints");
            PatchServiceEndpoints(exePath,
                options.Protocol == 0 ? "http" : "https",
                options.Hostname?.Trim()?.ToLowerInvariant(),
                options.ChangeXboxLiveAddress,
                options.ChangeXboxABAddress,
                options.ChangePlayfabApiAddress,
                options.XboxLiveProtocol.HasValue ? (options.XboxLiveProtocol.Value == 0 ? "http" : "https") : null,
                options.XboxLiveHostname,
                options.XboxABProtocol.HasValue ? (options.XboxABProtocol.Value == 0 ? "http" : "https") : null,
                options.XboxABHostname,
                options.PlayfabApiProtocol.HasValue ? (options.PlayfabApiProtocol.Value == 0 ? "http" : "https") : null,
                options.PlayfabApiHostname);
            Log.Debug("Done");
        }

        if (options.ForceInteractiveSignIn)
        {
            Log.Information("Forcing interactive sign-in");
            PatchForceInteractiveSignIn(exePath);
            Log.Debug("Done");
        }

        Log.Information("*****Building IPA*****");
        if (string.Equals(Path.GetFullPath(inIpa), Path.GetFullPath(outIpa), StringComparison.OrdinalIgnoreCase))
        {
            Log.Fatal("Input and output IPA paths must be different");
            return false;
        }

        File.Copy(inIpa, outIpa, true);

        string appEntryPrefix = $"Payload/{Path.GetFileName(appDir)}/";
        using (var zip = ZipFile.Open(outIpa, ZipArchiveMode.Update))
        {
            var toDelete = zip.Entries
                .Where(e => e.FullName.StartsWith(appEntryPrefix, StringComparison.OrdinalIgnoreCase)
                    && (IsDirectChildEntry(e.FullName, appEntryPrefix, "_CodeSignature")
                        || IsDirectChildEntry(e.FullName, appEntryPrefix, "SC_Info")
                        || IsDirectChildEntry(e.FullName, appEntryPrefix, "Zachary@Cracks")))
                .ToList();
            foreach (var entry in toDelete)
                entry.Delete();

            foreach (var (entryName, filePath) in new[]
            {
                ($"{appEntryPrefix}Info.plist", Path.Combine(appDir, "Info.plist")),
                ($"{appEntryPrefix}minecraftearthtf", exePath),
                ($"{appEntryPrefix}Frameworks/Xal.framework/Xal", Path.Combine(appDir, "Frameworks", "Xal.framework", "Xal")),
            })
            {
                if (!File.Exists(filePath))
                    continue;

                var originalEntry = zip.GetEntry(entryName);
                var originalAttributes = originalEntry?.ExternalAttributes ?? 0;
                originalEntry?.Delete();

                var newEntry = zip.CreateEntry(entryName, CompressionLevel.NoCompression);
                if (originalAttributes != 0)
                    newEntry.ExternalAttributes = originalAttributes;

                using (var entryStream = newEntry.Open())
                using (var fileStream = File.OpenRead(filePath))
                    fileStream.CopyTo(entryStream);
            }
        }

        if (Directory.Exists(decodedDir))
            Directory.Delete(decodedDir, true);

        Log.Information("Finished");
        return true;
    }

    private static byte[] AsciiToFixedBytes(string input, int maxLen)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(input);
        if (bytes.Length > maxLen)
            Array.Resize(ref bytes, maxLen);
        else if (bytes.Length < maxLen)
            Array.Resize(ref bytes, maxLen);
        return bytes;
    }

    private static readonly (long Site, int Rd, long Slot)[] XalAdrRedirects =
    {
        (0xB1C58, 1, 0x1000),
        (0xB2D8C, 0, 0x1080),
        (0xB1C74, 1, 0x1100),
        (0x98CF0, 1, 0x1100),
        (0xB2628, 0, 0x1180),
        (0xB26F8, 0, 0x1180),
        (0x98CD8, 1, 0x1200),
    };

    private static readonly byte[][] XalOriginalSites =
    {
        new byte[] { 0x01, 0xE1, 0x13, 0x70 },
        new byte[] { 0xE0, 0x61, 0x13, 0x10 },
        new byte[] { 0x01, 0xE1, 0x13, 0x10 },
        new byte[] { 0x21, 0x30, 0x1F, 0x30 },
        new byte[] { 0xC0, 0x9D, 0x13, 0x30 },
        new byte[] { 0x40, 0x97, 0x13, 0x30 },
        new byte[] { 0x61, 0x30, 0x1F, 0x50 },
    };

    private const long XalSchemeAddress = 0xD99BF;

    private static readonly (long Site, byte[] Original, byte[] Patched)[] XalWebViewDispatchSites =
    {
        (0x2B2D0, new byte[] { 0x0F, 0x00, 0x00, 0x14 }, new byte[] { 0x02, 0x00, 0x00, 0x14 }),
        (0x2B2D4, new byte[] { 0xA8, 0x01, 0x00, 0x34 }, new byte[] { 0x1F, 0x20, 0x03, 0xD5 }),
        (0x2B5E0, new byte[] { 0x48, 0x02, 0x00, 0x54 }, new byte[] { 0x4E, 0x02, 0x00, 0x54 }),
    };

    private const long ForceInteractiveSignInAddress = 0x6F9314;

    private static readonly byte[] ForceInteractiveSignInOriginal = { 0x62, 0x02, 0x00, 0x34 };
    private static readonly byte[] ForceInteractiveSignInPatched = { 0x1F, 0x20, 0x03, 0xD5 };

    private const long SpeedLimitGateAddress = 0x1869BD4;

    private static readonly byte[] SpeedLimitGateOriginal = { 0xCB, 0x06, 0x00, 0x54 };
    private static readonly byte[] SpeedLimitGatePatched = { 0x36, 0x00, 0x00, 0x14 };

    private static void PatchSpeedLimit(string exePath)
    {
        byte[] data = File.ReadAllBytes(exePath);

        byte[] actual = new byte[4];
        Array.Copy(data, SpeedLimitGateAddress, actual, 0, 4);
        if (!actual.SequenceEqual(SpeedLimitGateOriginal))
        {
            throw new InvalidDataException($"Unexpected bytes at speed limit gate 0x{SpeedLimitGateAddress:X} (found {Convert.ToHexString(actual)}, expected {Convert.ToHexString(SpeedLimitGateOriginal)}), the binary may be different from the supported version");
        }
        Array.Copy(SpeedLimitGatePatched, 0, data, SpeedLimitGateAddress, 4);

        File.WriteAllBytes(exePath, data);
    }

    private static void PatchForceInteractiveSignIn(string exePath)
    {
        byte[] data = File.ReadAllBytes(exePath);

        byte[] actual = new byte[4];
        Array.Copy(data, ForceInteractiveSignInAddress, actual, 0, 4);
        if (!actual.SequenceEqual(ForceInteractiveSignInOriginal))
        {
            throw new InvalidDataException($"Unexpected bytes at interactive sign-in dispatch site 0x{ForceInteractiveSignInAddress:X} (found {Convert.ToHexString(actual)}, expected {Convert.ToHexString(ForceInteractiveSignInOriginal)}), the binary may be different from the supported version");
        }
        Array.Copy(ForceInteractiveSignInPatched, 0, data, ForceInteractiveSignInAddress, 4);

        File.WriteAllBytes(exePath, data);
    }

    private static void PatchXalWebViewPresentation(string xalPath)
    {
        byte[] data = File.ReadAllBytes(xalPath);

        foreach (var (site, original, patched) in XalWebViewDispatchSites)
        {
            byte[] actual = new byte[4];
            Array.Copy(data, site, actual, 0, 4);
            if (!actual.SequenceEqual(original))
            {
                throw new InvalidDataException($"Unexpected bytes at Xal web view dispatch site 0x{site:X} (found {Convert.ToHexString(actual)}, expected {Convert.ToHexString(original)}), the binary may be different from the supported version");
            }
            Array.Copy(patched, 0, data, site, 4);
        }

        File.WriteAllBytes(xalPath, data);
    }

    private static void PatchXalAuth(string xalPath, string scheme, string hostname)
    {
        byte[] data = File.ReadAllBytes(xalPath);

        for (int i = 0; i < XalAdrRedirects.Length; i++)
        {
            long site = XalAdrRedirects[i].Site;
            byte[] actual = new byte[4];
            Array.Copy(data, site, actual, 0, 4);
            if (!actual.SequenceEqual(XalOriginalSites[i]))
            {
                throw new InvalidDataException($"Unexpected bytes at Xal ADR site 0x{site:X}, the binary may be different from the supported version");
            }
        }

        string[] slots =
        {
            $"{scheme}://{hostname}/auth.xboxlive.com",
            $"%s{hostname}/%s%s.xboxlive.com",
            $"{scheme}://{hostname}/xboxlive.com",
            $"{scheme}://{hostname}/%s.live%s.com",
            $"*.{hostname}",
        };

        for (int i = 0; i < slots.Length; i++)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(slots[i] + "\0");
            if (bytes.Length > 0x80)
            {
                throw new InvalidDataException($"Xal redirect string is too long for slot 0x{0x1000 + i * 0x80:X}");
            }
            Array.Copy(bytes, 0, data, 0x1000 + i * 0x80, bytes.Length);
        }

        foreach (var (site, rd, slot) in XalAdrRedirects)
        {
            Array.Copy(EncodeAdr(site, rd, slot), 0, data, site, 4);
        }

        if (scheme == "http")
        {
            data[XalSchemeAddress + 4] = 0x3A; // 's' -> ':'
            data[XalSchemeAddress + 5] = 0x2F; // ':' -> '/'
            data[XalSchemeAddress + 7] = 0x00; // '/' -> NUL
        }

        File.WriteAllBytes(xalPath, data);
    }

    private static void PatchInfoPlistAts(string appDir, params ReadOnlySpan<string?> hostnames)
    {
        string plistPath = Path.Combine(appDir, "Info.plist");
        if (!File.Exists(plistPath))
            return;

        string text = File.ReadAllText(plistPath);

        var hosts = new List<string>();
        foreach (var hostname in hostnames)
        {
            if (!string.IsNullOrWhiteSpace(hostname) && !hosts.Contains(hostname))
                hosts.Add(hostname);
        }

        string hostEntries = string.Join("", hosts.Select(h =>
            $"            <key>{EscapeXml(StripPort(h))}</key>\n" +
            "            <dict>\n" +
            "                <key>NSExceptionAllowsInsecureHTTPLoads</key>\n" +
            "                <true/>\n" +
            "                <key>NSIncludesSubdomains</key>\n" +
            "                <true/>\n" +
            "            </dict>\n"));

        string boolKeys =
            "        <key>NSAllowsArbitraryLoads</key>\n" +
            "        <true/>\n" +
            "        <key>NSAllowsLocalNetworking</key>\n" +
            "        <true/>\n" +
            "        <key>NSAllowsArbitraryLoadsInWebContent</key>\n" +
            "        <true/>\n";

        const string atsKey = "<key>NSAppTransportSecurity</key>";
        int atsKeyIdx = text.IndexOf(atsKey, StringComparison.Ordinal);

        if (atsKeyIdx >= 0)
        {
            int open = text.IndexOf("<dict>", atsKeyIdx, StringComparison.Ordinal);
            int close = FindDictClose(text, open);
            if (open < 0 || close < 0)
                return;

            text = text.Insert(open + "<dict>".Length, "\n" + boolKeys);

            int newOpen = text.IndexOf("<dict>", text.IndexOf(atsKey, StringComparison.Ordinal), StringComparison.Ordinal);
            int newClose = FindDictClose(text, newOpen);
            int exKeyIdx = text.IndexOf("<key>NSExceptionDomains</key>", newOpen, StringComparison.Ordinal);

            if (exKeyIdx >= 0 && exKeyIdx < newClose)
            {
                int exOpen = text.IndexOf("<dict>", exKeyIdx, StringComparison.Ordinal);
                int exClose = FindDictClose(text, exOpen);
                if (exOpen < 0 || exClose < 0)
                    return;
                text = text.Insert(exClose, hostEntries);
            }
            else
            {
                string exBlock =
                    "        <key>NSExceptionDomains</key>\n" +
                    "        <dict>\n" + hostEntries +
                    "        </dict>\n";
                text = text.Insert(newClose, exBlock);
            }
        }
        else
        {
            string atsBlock =
                "    <key>NSAppTransportSecurity</key>\n" +
                "    <dict>\n" + boolKeys +
                "        <key>NSExceptionDomains</key>\n" +
                "        <dict>\n" + hostEntries +
                "        </dict>\n" +
                "    </dict>\n";
            int plistClose = text.IndexOf("</plist>", StringComparison.Ordinal);
            if (plistClose < 0)
                return;
            text = text.Insert(plistClose, atsBlock);
        }

        WritePlist(plistPath, text);
    }

    private static string EscapeXml(string value)
    {
        return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static string StripPort(string host)
    {
        int colon = host.IndexOf(':');
        return colon > 0 ? host.Substring(0, colon) : host;
    }

    private static int FindDictClose(string text, int openIndex)
    {
        int depth = 0;
        int i = openIndex;
        while (i < text.Length)
        {
            if (text.IndexOf("<dict>", i, StringComparison.Ordinal) == i)
            {
                depth++;
                i += "<dict>".Length;
                continue;
            }
            if (text.IndexOf("</dict>", i, StringComparison.Ordinal) == i)
            {
                depth--;
                if (depth == 0)
                    return i;
                i += "</dict>".Length;
                continue;
            }
            i++;
        }
        return -1;
    }

    private static byte[] EncodeAdr(long pc, int rd, long target)
    {
        long imm = target - pc;
        if (imm < -0x100000 || imm > 0xFFFFF)
        {
            throw new InvalidDataException($"ADR out of range: 0x{pc:X} -> 0x{target:X}");
        }
        long immlo = imm & 3;
        long immhi = (imm >> 2) & 0x7FFFF;
        uint insn = (uint)(((immhi << 5) | 0x10000000) | (immlo << 29) | (uint)rd);
        return BitConverter.GetBytes(insn);
    }

    private const long MainSlotAreaOffset = 0x43F75E0;
    private const long MainSlotAreaSize = 0x43F8000 - 0x43F75E0;

    private sealed class EndpointSite
    {
        public string Group { get; set; }
        public long Adrp { get; set; }
        public long Add { get; set; }
        public int Rd { get; set; }
        public long OriginalVm { get; set; }
        public string Tail { get; set; }
    }

    private static readonly EndpointSite[] EndpointSites =
    {
        new() { Group = "xboxlive", Adrp = 0xB406CC, Add = 0xB406D0, Rd = 8, OriginalVm = 0x1041973C4, Tail = "clubaccounts.xboxlive.com" },
        new() { Group = "xboxlive", Adrp = 0xB4072C, Add = 0xB40730, Rd = 8, OriginalVm = 0x1041973E6, Tail = "avty.xboxlive.com" },
        new() { Group = "xboxlive", Adrp = 0xB40784, Add = 0xB40788, Rd = 8, OriginalVm = 0x104197400, Tail = "comments.xboxlive.com" },
        new() { Group = "xboxlive", Adrp = 0xB407D8, Add = 0xB407DC, Rd = 8, OriginalVm = 0x10419741E, Tail = "clubpresence.xboxlive.com" },
        new() { Group = "xboxlive", Adrp = 0xB40824, Add = 0xB40828, Rd = 8, OriginalVm = 0x104197440, Tail = "clubhub.xboxlive.com" },
        new() { Group = "xboxlive", Adrp = 0xB40880, Add = 0xB40884, Rd = 8, OriginalVm = 0x10419745D, Tail = "clubmoderation.xboxlive.com/" },
        new() { Group = "xboxlive", Adrp = 0xB408D8, Add = 0xB408DC, Rd = 8, OriginalVm = 0x104197482, Tail = "reputation.xboxlive.com/" },
        new() { Group = "xboxlive", Adrp = 0xB4091C, Add = 0xB40920, Rd = 8, OriginalVm = 0x1041974A3, Tail = "mediahub.xboxlive.com" },
        new() { Group = "xboxlive", Adrp = 0xB4096C, Add = 0xB40970, Rd = 8, OriginalVm = 0x1041974C1, Tail = "clubprofile.xboxlive.com" },
        new() { Group = "xboxlive", Adrp = 0xB409BC, Add = 0xB409C0, Rd = 8, OriginalVm = 0x1041974E2, Tail = "userposts.xboxlive.com" },
        new() { Group = "xboxlive", Adrp = 0xB654E0, Add = 0xB654E4, Rd = 8, OriginalVm = 0x1041976B8, Tail = "xforge.xboxlive.com/" },
        new() { Group = "xboxab", Adrp = 0xB6359C, Add = 0xB635A0, Rd = 8, OriginalVm = 0x104197696, Tail = "www.xboxab.com" },
        new() { Group = "playfab", Adrp = 0xDAFA5C, Add = 0xDAFA60, Rd = 3, OriginalVm = 0x10419FCFE, Tail = "playfab.com" },
        new() { Group = "playfab", Adrp = 0xDAFAA4, Add = 0xDAFAA8, Rd = 3, OriginalVm = 0x10419FD12, Tail = "playfab.com/6AF55D1D10B42870/" },
        new() { Group = "playfab", Adrp = 0xDAFAE4, Add = 0xDAFAE8, Rd = 3, OriginalVm = 0x10419FD38, Tail = "playfab.com/B63A0803D3653643/" },
    };

    private static void PatchServiceEndpoints(string exePath, string scheme, string hostname, bool xboxlive, bool xboxab, bool playfab,
        string? xboxliveScheme = null, string? xboxliveHostname = null,
        string? xboxabScheme = null, string? xboxabHostname = null,
        string? playfabScheme = null, string? playfabHostname = null)
    {
        byte[] data = File.ReadAllBytes(exePath);

        var enabled = new List<EndpointSite>();
        foreach (var site in EndpointSites)
        {
            bool include = site.Group switch
            {
                "xboxlive" => xboxlive,
                "xboxab" => xboxab,
                "playfab" => playfab,
                _ => false,
            };
            if (!include) continue;

            uint adrp = BitConverter.ToUInt32(data, (int)site.Adrp);
            uint add = BitConverter.ToUInt32(data, (int)site.Add);
            long pc = site.Adrp + 0x100000000L;
            long pcPage = pc & ~0xFFFL;
            long immlo = (adrp >> 29) & 3;
            long immhi = (adrp >> 5) & 0x7FFFF;
            long imm = (immhi << 2) | immlo;
            if ((imm & 0x100000) != 0) imm -= 0x200000;
            long targetPage = pcPage + (imm << 12);
            int rd = (int)(adrp & 0x1F);
            int rn = (int)((add >> 5) & 0x1F);
            long imm12 = (add >> 10) & 0xFFF;
            bool valid = (adrp & 0x9F000000) == 0x90000000
                && (add & 0xFF800000) == 0x91000000
                && rd == site.Rd && rn == site.Rd
                && (targetPage + imm12) == site.OriginalVm;
            if (!valid)
            {
                throw new InvalidDataException($"Unexpected bytes at service endpoint site 0x{site.Adrp:X}, the binary may be different from the supported version");
            }

            enabled.Add(site);
        }

        long cursor = MainSlotAreaOffset;
        var slotVm = new Dictionary<long, long>();
        foreach (var site in enabled)
        {
            string siteScheme = site.Group switch
            {
                "xboxlive" => xboxliveScheme ?? scheme,
                "xboxab" => xboxabScheme ?? scheme,
                "playfab" => playfabScheme ?? scheme,
                _ => scheme,
            };
            string siteHostname = site.Group switch
            {
                "xboxlive" => xboxliveHostname ?? hostname,
                "xboxab" => xboxabHostname ?? hostname,
                "playfab" => playfabHostname ?? hostname,
                _ => hostname,
            };

            byte[] bytes = Encoding.ASCII.GetBytes($"{siteScheme}://{siteHostname}/{site.Tail}\0");
            if (bytes.Length > MainSlotAreaOffset + MainSlotAreaSize - cursor)
            {
                throw new InvalidDataException("Service endpoint redirect strings are too long for the available free space");
            }
            Array.Copy(bytes, 0, data, cursor, bytes.Length);
            slotVm[site.Adrp] = cursor + 0x100000000L;
            cursor += bytes.Length;
        }

        foreach (var site in enabled)
        {
            long targetVm = slotVm[site.Adrp];
            Array.Copy(EncodeAdrp(site.Adrp + 0x100000000L, site.Rd, targetVm & ~0xFFFL), 0, data, site.Adrp, 4);
            Array.Copy(EncodeAddImm(site.Rd, site.Rd, targetVm & 0xFFF), 0, data, site.Add, 4);
        }

        File.WriteAllBytes(exePath, data);
    }

    private static byte[] EncodeAdrp(long pcVm, int rd, long targetPage)
    {
        long pcPage = pcVm & ~0xFFFL;
        long delta = (targetPage - pcPage) >> 12;
        if (delta < -0x80000 || delta > 0x7FFFF)
        {
            throw new InvalidDataException($"ADRP out of range: 0x{pcVm:X} -> 0x{targetPage:X}");
        }
        long immlo = delta & 3;
        long immhi = (delta >> 2) & 0x7FFFF;
        uint insn = 0x90000000u | ((uint)immhi << 5) | ((uint)immlo << 29) | (uint)rd;
        return BitConverter.GetBytes(insn);
    }

    private static byte[] EncodeAddImm(int rd, int rn, long imm12)
    {
        uint insn = 0x91000000u | ((uint)imm12 << 10) | ((uint)rn << 5) | (uint)rd;
        return BitConverter.GetBytes(insn);
    }

    private static void PatchAppName(string appDir, string newName)
    {
        string plistPath = Path.Combine(appDir, "Info.plist");
        if (!File.Exists(plistPath))
            return;

        string text = File.ReadAllText(plistPath);
        string escaped = newName.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        foreach (var keyName in new[] { "CFBundleDisplayName", "CFBundleName" })
        {
            string keyTag = $"<key>{keyName}</key>";
            int keyIdx = text.IndexOf(keyTag, StringComparison.Ordinal);
            while (keyIdx >= 0)
            {
                int open = text.IndexOf("<string>", keyIdx + keyTag.Length, StringComparison.Ordinal);
                int close = text.IndexOf("</string>", open, StringComparison.Ordinal);
                if (open < 0 || close < 0)
                    break;

                int valueStart = open + "<string>".Length;
                text = text.Substring(0, valueStart) + escaped + text.Substring(close);
                keyIdx = text.IndexOf(keyTag, close, StringComparison.Ordinal);
            }
        }

        WritePlist(plistPath, text);
    }

    private static void WritePlist(string plistPath, string text)
    {
        File.WriteAllText(plistPath, text, new UTF8Encoding(false));
    }

    private static void GlobalReplaceBytes(string filePath, byte[] search, byte[] replace)
    {
        byte[] data = File.ReadAllBytes(filePath);
        for (int i = 0; i <= data.Length - search.Length; i++)
        {
            bool found = true;
            for (int j = 0; j < search.Length; j++)
            {
                if (data[i + j] != search[j])
                {
                    found = false;
                    break;
                }
            }
            if (found)
            {
                Array.Copy(replace, 0, data, i, replace.Length);
                i += replace.Length - 1;
            }
        }
        File.WriteAllBytes(filePath, data);
    }

    private static void PatchAtOffset(string filePath, long offset, byte[] data)
    {
        using (var fs = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            fs.Write(data, 0, data.Length);
        }
    }

    private static void RemoveDrm(string appDir)
    {
        foreach (var dir in Directory.GetDirectories(appDir, "_CodeSignature", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetDirectories(appDir, "SC_Info", SearchOption.TopDirectoryOnly)))
        {
            Directory.Delete(dir, true);
        }
    }

    private static bool IsDirectChildEntry(string fullName, string prefix, string child)
    {
        if (fullName.Length <= prefix.Length || !fullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string relative = fullName.Substring(prefix.Length);
        return relative.StartsWith(child, StringComparison.OrdinalIgnoreCase);
    }

#pragma warning disable CS8618
    public sealed class Options
    {
        public bool Autonomous { get; set; }
        public string InIpa { get; set; }
        public string OutIpa { get; set; }
        public int Protocol { get; set; }
        public string Hostname { get; set; }
        public int LocatorProtocol { get; set; }
        public string LocatorHostname { get; set; }
        public int? XalProtocol { get; set; }
        public string XalHostname { get; set; }
        public int? PlayfabApiProtocol { get; set; }
        public string PlayfabApiHostname { get; set; }
        public int? XboxABProtocol { get; set; }
        public string XboxABHostname { get; set; }
        public int? XboxLiveProtocol { get; set; }
        public string XboxLiveHostname { get; set; }
        public string AppName { get; set; }
        public string DecodedDir { get; set; } = "IpaDecoded";
        public bool ChangeLocatorAddress { get; set; } = true;
        public bool ChangeXalAuthAddress { get; set; } = true;
        public bool ForceInAppWebView { get; set; } = true;
        public bool ForceInteractiveSignIn { get; set; } = true;
        public bool ChangePlayfabApiAddress { get; set; } = true;
        public bool ChangeXboxABAddress { get; set; } = true;
        public bool ChangeXboxLiveAddress { get; set; } = true;
        public bool DisableSunsetTimeCheck { get; set; } = true;
        public bool DisableSpeedLimit { get; set; } = false;
        public bool ChangeAppName { get; set; } = true;
        public bool ChangeIcon { get; set; } = false;
        public string IconPath { get; set; } = "";
        public bool RemoveDrm { get; set; } = true;
    }
#pragma warning restore CS8618
}
