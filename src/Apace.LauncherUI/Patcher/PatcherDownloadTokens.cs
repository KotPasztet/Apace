using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Apace.LauncherUI.Patcher;

/// <summary>
/// Self-contained signed URLs for the patched-client download
/// (<c>/api/patcher/dl/{jobId}/{expiry}/{token}</c>).
///
/// The cookie-authorized route (<c>/api/patcher/download/{jobId}</c>) requires the
/// panel's Identity session, which only exists inside the browser/WebView that is
/// logged in. On Android the launcher hands download links to the system browser
/// (Chrome) so the download goes through the platform's own flow - download
/// notification, "open with" chooser, package installer. An external browser has
/// no session cookie, so the download link must authorize itself: the URL embeds
/// the job id, a unix-time expiry and an HMAC-SHA256 over both, keyed with a
/// random per-install secret persisted next to the patcher data (survives
/// updates - the data dir is preserved by every update path).
///
/// Tokens are stateless and minted on page render; nothing needs cleanup. The
/// lifetime is deliberately long (7 days) - the token only grants the same file
/// the user can already download, and the file itself disappears when the job
/// is pruned, at which point the endpoint returns 404 regardless of the token.
/// </summary>
internal sealed class PatcherDownloadTokens
{
    private const string KeyFileName = "download-token.key";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private readonly byte[] _key;

    public PatcherDownloadTokens()
    {
        Directory.CreateDirectory(PatcherService.BaseDir);
        var keyPath = Path.Combine(PatcherService.BaseDir, KeyFileName);

        byte[]? key = null;
        try
        {
            if (File.Exists(keyPath))
            {
                key = File.ReadAllBytes(keyPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unreadable key file - generate a fresh key below; old links die
        }

        if (key is not { Length: 32 })
        {
            key = RandomNumberGenerator.GetBytes(32);
            try
            {
                File.WriteAllBytes(keyPath, key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort - an unpersisted key still works for this run,
                // but signed URLs stop validating after a panel restart
            }
        }

        _key = key;
    }

    /// <summary>Absolute-free app-relative download URL for the given job.</summary>
    public string CreateUrl(string jobId)
    {
        var expiry = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
        return $"/api/patcher/dl/{jobId}/{expiry}/{Compute(jobId, expiry)}";
    }

    public bool Validate(string jobId, long expiry, string? token)
    {
        if (string.IsNullOrEmpty(token) || expiry <= 0)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry)
        {
            return false;
        }

        byte[] expected;
        byte[] provided;
        try
        {
            expected = ComputeBytes(jobId, expiry);
            provided = WebEncoders.Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private string Compute(string jobId, long expiry)
    {
        return WebEncoders.Base64UrlEncode(ComputeBytes(jobId, expiry));
    }

    private byte[] ComputeBytes(string jobId, long expiry)
    {
        using var hmac = new HMACSHA256(_key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes($"patcher-download|{jobId}|{expiry}"));
    }
}
