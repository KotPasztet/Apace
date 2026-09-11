using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Apace.ApiServer;

public sealed record class Config(Config.LoginR Login, Config.XboxLiveR XboxLive, Config.PlayfabApiR PlayfabApi)
{
    private const int TokenSecretByteLength = 64;
    private const int SessionKeyByteLength = 32;

    // the secrets are generated randomly per installation (and persisted in the freshly created api_config.json),
    // the previous hardcoded defaults were the public upstream Solace secrets, so anyone could forge tokens for any server using them
    public static readonly Config Default = new Config
    (
        new LoginR(
            SoapHeaderValidityMinutes: 1,
            UserTokenValidityMinutes: 7 * 24 * 60,
            DeviceTokenValidityMinutes: 1,
            XboxTokenValidityMinutes: 7 * 24 * 60,
            UserTokenSecret: GenerateSecret(),
            DeviceTokenSecret: GenerateSecret(),
            XboxTokenSecret: GenerateSecret(),
            UserTokenSessionKey: GenerateSecret(SessionKeyByteLength)
        ),
        new XboxLiveR(
            TokenValidityMinutes: 7 * 24 * 60,
            AuthTokenSecret: GenerateSecret(),
            XapiTokenSecret: GenerateSecret(),
            PlayfabTokenSecret: GenerateSecret()
        ),
        new PlayfabApiR(
            EntityTokenValidityMinutes: 24 * 60,
            SessionTicketValidityMinutes: 24 * 60,
            EntityTokenSecret: GenerateSecret(),
            SessionTicketSecret: GenerateSecret()
        )
    );

    public static string GenerateSecret(int byteLength = TokenSecretByteLength)
    {
        byte[] bytes = new byte[byteLength];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    public sealed record LoginR(
        int SoapHeaderValidityMinutes,
        int UserTokenValidityMinutes,
        int DeviceTokenValidityMinutes,
        int XboxTokenValidityMinutes,
        string UserTokenSecret,
        string DeviceTokenSecret,
        string XboxTokenSecret,
        string UserTokenSessionKey
    )
    {
        private byte[]? _userTokenSecretBytes;
        private byte[]? _deviceTokenSecretBytes;
        private byte[]? _xboxTokenSecretBytes;
        private byte[]? _userTokenSessionKeyBytes;

        [JsonIgnore]
        public byte[] UserTokenSecretBytes => _userTokenSecretBytes ??= Convert.FromBase64String(UserTokenSecret);

        [JsonIgnore]
        public byte[] DeviceTokenSecretBytes => _deviceTokenSecretBytes ??= Convert.FromBase64String(DeviceTokenSecret);

        [JsonIgnore]
        public byte[] XboxTokenSecretBytes => _xboxTokenSecretBytes ??= Convert.FromBase64String(XboxTokenSecret);

        [JsonIgnore]
        public byte[] UserTokenSessionKeyBytes => _userTokenSessionKeyBytes ??= Convert.FromBase64String(UserTokenSessionKey);
    }

    public sealed record XboxLiveR(
        int TokenValidityMinutes,
        string AuthTokenSecret,
        string XapiTokenSecret,
        string PlayfabTokenSecret
    )
    {
        private byte[]? _authTokenSecretBytes;
        private byte[]? _xapiTokenSecretBytes;
        private byte[]? _playfabTokenSecretBytes;

        [JsonIgnore]
        public byte[] AuthTokenSecretBytes => _authTokenSecretBytes ??= Convert.FromBase64String(AuthTokenSecret);

        [JsonIgnore]
        public byte[] XapiTokenSecretBytes => _xapiTokenSecretBytes ??= Convert.FromBase64String(XapiTokenSecret);

        [JsonIgnore]
        public byte[] PlayfabTokenSecretBytes => _playfabTokenSecretBytes ??= Convert.FromBase64String(PlayfabTokenSecret);
    }

    public sealed record PlayfabApiR(
        int EntityTokenValidityMinutes,
        int SessionTicketValidityMinutes,
        string EntityTokenSecret,
        string SessionTicketSecret
    )
    {
        private byte[]? _entityTokenSecretBytes;
        private byte[]? _sessionTicketSecretBytes;

        [JsonIgnore]
        public byte[] EntityTokenSecretBytes => _entityTokenSecretBytes ??= Convert.FromBase64String(EntityTokenSecret);

        [JsonIgnore]
        public byte[] SessionTicketSecretBytes => _sessionTicketSecretBytes ??= Convert.FromBase64String(SessionTicketSecret);
    }
}
