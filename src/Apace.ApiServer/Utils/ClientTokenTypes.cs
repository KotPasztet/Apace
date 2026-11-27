using System.Text;

namespace Apace.ApiServer.Utils;

// the client matches clientType by its FNV-1a hash
internal static class ClientTokenTypes
{
    public const string AdventureReward = "adventure.reward.khx6bcq";
    public const uint AdventureRewardHash = 0xe37ab0e7;

    public static uint Fnv1a32(string value)
    {
        uint hash = 0x811c9dc5;
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 0x01000193;
        }

        return hash;
    }
}
