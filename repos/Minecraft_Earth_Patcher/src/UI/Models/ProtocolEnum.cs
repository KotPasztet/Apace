using System.ComponentModel;

namespace MCEPatcher.UI.Models;

public enum ProtocolEnum
{
    Http = 0,
    Https = 1
}

public static class ProtocolEnumExtensions
{
    extension(ProtocolEnum protocol)
    {
        public string ToProtocolString()
            => protocol switch
            {
                ProtocolEnum.Http => "http",
                ProtocolEnum.Https => "https",
                _ => throw new InvalidEnumArgumentException(nameof(protocol), (int)protocol, typeof(ProtocolEnum)),
            };
    }
}
