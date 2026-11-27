using System.Text.Json.Serialization;
using Apace.Common;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Apace.Buildplate.Connector.Model;
#pragma warning restore IDE0130 // Namespace does not match folder structure

public sealed record AdventureChestBrokenMessage(
    string MinecraftId,
    string MinecraftName,
    string BlockState,
    [property: JsonRequired] int X,
    [property: JsonRequired] int Y,
    [property: JsonRequired] int Z
)
{
    // anything unexpected is treated as no message rather than as an error
    public static AdventureChestBrokenMessage? Read(string json)
    {
        AdventureChestBrokenMessage? message;
        try
        {
            message = Json.Deserialize<AdventureChestBrokenMessage>(json);
        }
        catch (Exception)
        {
            return null;
        }

        return message is not null && message.IsComplete ? message : null;
    }

    [JsonIgnore]
    public bool IsComplete => !string.IsNullOrEmpty(MinecraftId) && !string.IsNullOrEmpty(MinecraftName) && !string.IsNullOrEmpty(BlockState);
}
