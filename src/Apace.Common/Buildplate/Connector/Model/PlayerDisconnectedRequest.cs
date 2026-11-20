#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Apace.Buildplate.Connector.Model;
#pragma warning restore IDE0130 // Namespace does not match folder structure

// Health is the player's health when they left the buildplate server, as
// reported by the Fabric server through the bridge. Null when the connector
// plugin is an older version that does not report it.
public sealed record PlayerDisconnectedRequest(
     string PlayerId,
     InventoryResponse? BackpackContents,
     float? Health
);
