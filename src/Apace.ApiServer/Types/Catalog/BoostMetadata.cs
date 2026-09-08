using Apace.ApiServer.Types.Common;

namespace Apace.ApiServer.Types.Catalog;

public sealed record BoostMetadata(
    string Name,
    string Type,
    string Attribute,
    bool CanBeDeactivated,
    bool CanBeRemoved,
    string? ActiveDuration,
    bool Additive,
    int? Level,
    Effect[] Effects,
    string? Scenario,
    string? Cooldown
);
