using System.Text.Json.Serialization;

#nullable disable
namespace MCEPatcher.Core;

public class BinaryVariable
{
    public string File { get; set; }
    public string HexAddress
    {
        get => Address.ToString("x8");
        set => Address = Convert.ToInt32(value, 16);
    }
    [JsonIgnore]
    public int Address;
    public string TemplateString { get; set; }
}
