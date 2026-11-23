using System.Buffers.Binary;
using System.Globalization;

namespace Apace.ApiServer.Utils;

// Accounts are identified by a hex user id, which the Android client accepts as an xuid.
// The iOS XAL client requires a numeric (uint64) xuid, so derive a stable one from the id.
internal static class XuidUtils
{
    public static ulong FromUserId(string userId)
    {
        byte[] bytes = Convert.FromHexString(userId);

        // Keep it in the range of real xuids (2535...) so clients treat it like one
        return 0x0009_0000_0000_0000UL | (BinaryPrimitives.ReadUInt64LittleEndian(bytes) & 0x0000_FFFF_FFFF_FFFFUL);
    }

    public static string ToNumericString(string userId)
        => FromUserId(userId).ToString(CultureInfo.InvariantCulture);

    public static bool Matches(string xuid, string userId)
        => xuid == userId
            || (ulong.TryParse(xuid, NumberStyles.None, CultureInfo.InvariantCulture, out ulong numeric) && numeric == FromUserId(userId));
}
