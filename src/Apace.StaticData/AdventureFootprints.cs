using System.Collections.Frozen;
using System.Text.Json.Serialization;
using Apace.Common;

namespace Apace.StaticData;

public sealed class AdventureFootprints
{
    public const string FileName = "footprints.json";

    private readonly FrozenDictionary<string, Footprint> _footprints;

    internal AdventureFootprints(string dir)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(dir, FileName));
            FootprintsFile? file = Json.Deserialize<FootprintsFile>(stream);
            Dictionary<string, Footprint> footprints = file?.Footprints ?? [];
            foreach ((string id, Footprint footprint) in footprints)
            {
                if (footprint is null || footprint.X <= 0 || footprint.Z <= 0)
                {
                    throw new InvalidDataException($"Footprint of {id} must have a positive x and z");
                }
            }

            _footprints = footprints.ToFrozenDictionary();
        }
        catch (Exception exception)
        {
            throw new StaticDataException(null, exception);
        }
    }

    public int Count => _footprints.Count;

    public Footprint? GetFootprint(string templateId)
        => _footprints.GetValueOrDefault(templateId);

    public sealed record Footprint(
        [property: JsonRequired] int X,
        [property: JsonRequired] int Z,
        FootprintOffset? Offset = null
    );

    public sealed record FootprintOffset(
        [property: JsonRequired] double X,
        [property: JsonRequired] double Z,
        double Y = 0
    );

    private sealed record FootprintsFile(
        Dictionary<string, Footprint>? Footprints
    );
}
