using Apace.ApiServer.Types.Common;

namespace Apace.ApiServer.Types.Catalog;

public sealed record NFCBoost(
    string Id,
    string Name,
    string Type,
    Rewards Rewards,
    BoostMetadata BoostMetadata,
    bool Deprecated,
    string ToolsVersion
);
