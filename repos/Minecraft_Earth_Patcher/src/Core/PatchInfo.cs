using Serilog;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable disable
namespace MCEPatcher.Core;

public sealed class PatchInfo
{
    public static readonly PatchInfo Default = new PatchInfo()
    {
        Prerequisites = new(),
        VariablesUsed = new(),
        BinaryVariables = new(),
    };

    [JsonIgnore]
    public string Path { get; set; }

    /// <summary>
    /// Patches that need to be done before this patch
    /// </summary>
    public List<Prerequisite> Prerequisites { get; set; }
    /// <summary>
    /// Variables used in this patch (locatorprotocol, locatorhostname, ...)
    /// </summary>
    public List<string> VariablesUsed { get; set; }
    /// <summary>
    /// <see cref="BinaryVariable"/>s in this patch
    /// </summary>
    public List<BinaryVariable> BinaryVariables { get; set; }

#nullable enable
    public static PatchInfo Load(string path, string name)
    {
        PatchInfo? patch;
        if (File.Exists(path))
        {
            try
            {
                patch = JsonSerializer.Deserialize<PatchInfo>(File.ReadAllText(path));
                if (patch is null)
                {
                    throw new JsonException("Json is null");
                }
            }
            catch (Exception ex)
            {
                Log.Fatal($"Couldn't deserialize info for patch '{name}', ex: {ex}");
                U.PAKE();
                Environment.Exit(1);
                return null;
            }
        }
        else
        {
            patch = Default;
        }

        patch.Path = System.IO.Path.GetFullPath(System.IO.Path.Combine("Patches", $"{name}.patch"));

        return patch;
    }
}
