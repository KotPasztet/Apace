namespace Apace.ApiServer;

// The public upstream Solace token signing secrets.
//
// Apace (and Solace before it) used to sign every login token with these
// hardcoded values, which made them effectively public: anyone could forge
// tokens for any server running the public build. They are now used ONLY to
// VERIFY tokens that game clients cached before the switch to
// per-installation random secrets (api_config.json):
//
// Clients keep their cached RPS and user tickets for up to 7 days and never
// re-request them while unexpired — they cannot know the server rotated its
// secrets, so after an update every player holding a pre-update ticket gets
// their account login rejected with 401 in a loop (user/authenticate) and
// the client never falls back to the password flow. Accepting the legacy
// secrets for verification unblocks those cached tickets; everything the
// server issues afterwards is signed with the installation's own random
// secrets, so the legacy path fades out as tickets expire or players
// re-login.
//
// Disable the fallback with "AcceptLegacyTokenSecrets": false in
// api_config.json once no client needs it anymore — the ApiServer log warns
// every time a legacy secret is still required.
internal static class LegacyTokenSecrets
{
    public const string UserTokenSecret = "Mf5HWU566mwFuxxyXa2ACPvZVw9DTfzO4DREWk0aoxfkaVEhM6OfJRQ2MR1FhtPpVgkhEBBBG1PJvjy6LoO90A==";
    public const string DeviceTokenSecret = "2MonNUihCGLzZRhMMkZ6GgFFnxj0Jk60Mvhoa2NVaOW51cDd4ZKD8L5RAbgcO1R9vfs4V/JZE6KmWW16I0OesQ==";
    public const string XboxTokenSecret = "Q/cQFxZs/PahNgsNrvEOUAQ6RQ45MTAaRXH9LNpSrZpjQ99RBmyxuJwOcnkX6daCuVqdo8/eefpe1wUamn9YTA==";

    private static byte[]? _userTokenSecretBytes;
    private static byte[]? _deviceTokenSecretBytes;
    private static byte[]? _xboxTokenSecretBytes;

    public static byte[] UserTokenSecretBytes => _userTokenSecretBytes ??= Convert.FromBase64String(UserTokenSecret);

    public static byte[] DeviceTokenSecretBytes => _deviceTokenSecretBytes ??= Convert.FromBase64String(DeviceTokenSecret);

    public static byte[] XboxTokenSecretBytes => _xboxTokenSecretBytes ??= Convert.FromBase64String(XboxTokenSecret);
}
