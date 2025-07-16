using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ViennaDotNet.TileRenderer;

public readonly struct RenderContext
{
    private readonly List<string> _tags;
    private readonly Dictionary<string, Dictionary<string, RenderLayer>> _tagsMap;

    public RenderContext(List<string> tags, Dictionary<string, Dictionary<string, RenderLayer>> tagsMap)
    {
        _tags = tags;
        _tagsMap = tagsMap;
    }

    public readonly ReadOnlySpan<string> Tags => CollectionsMarshal.AsSpan(_tags);

    public bool TryGetLayer(string tagName, string tagValue, out RenderLayer targetLayer)
    {
        if (_tagsMap.TryGetValue(tagName, out var valMap))
        {
            if (valMap.TryGetValue(tagValue, out var layer))
            {
                targetLayer = layer;
                return true;
            }
            else if (valMap.TryGetValue("*", out var defaultLayer))
            {
                targetLayer = defaultLayer;
                return true;
            }
        }

        targetLayer = default;
        return false;
    }
}
