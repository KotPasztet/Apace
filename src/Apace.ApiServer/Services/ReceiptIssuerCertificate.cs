using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Apace.ApiServer.Services;

// The receipt issuer certificate and its private key — the PlayFab Economy v2
// receipt validation pair.
//
// The MCE client validates the CONTENT KEY receipt that rides on
// /inventory/GetInventoryItems (InventoryController) before it registers the
// entitlement keys: it fetches the issuer certificate from
// /inventory/GetReceiptIssuerCertificateAsPem and verifies the receipt's JWS
// signature against the certificate's public key. The real PlayFab service
// answers with {"code":200,"status":"OK","data":{"PemCert":"-----BEGIN
// CERTIFICATE-----…"}} — a Microsoft TLS CA issued certificate for
// "mcreceiptissuer.inventory.prod.economy.playfab.com" (probed against the
// live service 2026-11-25). Apace cannot hold a Microsoft-signed certificate,
// so it serves its own self-signed one — but then the receipt MUST be signed
// with the matching private key or a verifying client rejects it (the previous
// alg=none receipt can never verify against ANY certificate).
//
// The pair is generated once and PERSISTED in the data volume next to the
// persona catalog state, so every restart serves the same certificate: clients
// that cached it keep validating, and receipts signed in earlier sessions
// still verify. The PEM is served byte-stable (the exact file contents, CRLF
// line endings like the real service).
//
// Everything degrades gracefully: a disk that cannot be written still yields a
// working in-memory pair for this run (the client re-fetches the certificate
// per receipt validation), and a key that cannot sign makes
// InventoryController fall back to the old alg=none receipt shape.
public static class ReceiptIssuerCertificate
{
    // Same volume directory as the persona catalog state (probe-state.json,
    // packs.json) — the persistent data volume, survives image updates.
    private static string Directory => Path.Combine(Program.MojangAssetsDir, LivePersonaCatalog.CatalogDirName);
    private static string CertificatePath => Path.Combine(Directory, "receipt-issuer.pem");
    private static string KeyPath => Path.Combine(Directory, "receipt-issuer.key.pem");

    // The subject of the real PlayFab receipt issuer certificate (probed
    // 2026-11-25) — mirrored so a client that inspects the subject name sees
    // the identity it expects.
    private const string SubjectName = "CN=mcreceiptissuer.inventory.prod.economy.playfab.com, O=Microsoft Corporation, L=Redmond, S=WA, C=US";

    private static readonly object LoadLock = new();
    private static (string Pem, RSA Key)? _pair;

    /// <summary>The receipt issuer certificate as a PEM string (CRLF line
    /// endings, exactly like the real service serves). Loads the persisted
    /// copy or generates a fresh pair. Disk problems degrade to an in-memory
    /// pair for the process; only a broken crypto stack (RSA/X509 unavailable
    /// process-wide — not a real scenario on the supported runtimes) throws,
    /// leaving the caller's response a 500 like the old missing endpoint.</summary>
    public static string CertificatePem
    {
        get
        {
            lock (LoadLock)
            {
                _pair ??= LoadFromDisk() ?? CreateAndPersist(Log.Logger);
                return _pair.Value.Pem;
            }
        }
    }

    /// <summary>Signs <paramref name="data"/> with the receipt issuer key
    /// (RS256: RSA PKCS#1 v1.5 with SHA-256 — the JWS algorithm matching the
    /// certificate's RSA key). False when no key is available (never in
    /// practice; the caller falls back to the unsigned receipt shape).</summary>
    public static bool TrySignRS256(byte[] data, out byte[] signature)
    {
        try
        {
            lock (LoadLock)
            {
                _pair ??= LoadFromDisk() ?? CreateAndPersist(Log.Logger);
                signature = _pair.Value.Key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not sign the inventory receipt with the receipt issuer key — serving the unsigned (alg=none) receipt shape");
            signature = [];
            return false;
        }
    }

    // The persisted pair, or null when either file is missing/unreadable. A
    // mismatched pair (cert without key) is rejected too — a certificate the
    // key cannot back can never satisfy a signature-verifying client.
    private static (string Pem, RSA Key)? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(CertificatePath) || !File.Exists(KeyPath))
            {
                return null;
            }

            string pem = File.ReadAllText(CertificatePath);
            if (!pem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            {
                Log.Warning("Receipt issuer certificate at {Path} is not a PEM certificate — regenerating", CertificatePath);
                return null;
            }

            using var _ = X509Certificate2.CreateFromPem(pem); // parses or throws
            var key = RSA.Create();
            key.ImportFromPem(File.ReadAllText(KeyPath));

            Log.Debug("Receipt issuer certificate loaded from {Path}", CertificatePath);
            return (pem, key);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the receipt issuer certificate from {Path} — regenerating", CertificatePath);
            return null;
        }
    }

    private static (string Pem, RSA Key) CreateAndPersist(ILogger logger)
    {
        var key = RSA.Create(2048);
        string pem;
        try
        {
            // Self-signed, like a real issuer certificate but without the CA
            // chain (Apace cannot obtain a Microsoft TLS CA signature). Valid
            // for ten years so the persisted pair outlives the frozen client.
            var request = new CertificateRequest(
                SubjectName,
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));

            // positive random serial, like a CA-issued certificate
            byte[] serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7F;

            using X509Certificate2 certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(10));
            pem = NormalizePem(certificate.ExportCertificatePem());
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Could not generate the receipt issuer certificate — receipt validation cannot succeed this run");
            throw;
        }

        try
        {
            DirectoryCreate();
            WriteFileAtomic(CertificatePath, pem);
            WriteFileAtomic(KeyPath, NormalizePem(key.ExportPkcs8PrivateKeyPem()));
            logger.Information("Receipt issuer certificate generated and persisted at {Path} (subject \"{Subject}\") — /inventory/GetReceiptIssuerCertificateAsPem serves it, inventory receipts are RS256-signed with its key",
                CertificatePath, SubjectName);
        }
        catch (Exception ex)
        {
            // the pair still works for this run; the client re-fetches the
            // certificate whenever it validates a receipt, so a fresh pair per
            // process is merely wasteful, not broken
            logger.Warning(ex, "Could not persist the receipt issuer certificate to {Path} — using the in-memory pair for this run", CertificatePath);
        }

        return (pem, key);
    }

    // PEM with CRLF line endings, exactly like the real service serves
    // (ExportCertificatePem uses LF).
    private static string NormalizePem(string pem)
        => pem.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    private static void DirectoryCreate()
    {
        string? parent = Path.GetDirectoryName(CertificatePath);
        if (parent is not null)
        {
            System.IO.Directory.CreateDirectory(parent);
        }
    }

    // tmp + move, the same pattern the persona catalog state files use
    private static void WriteFileAtomic(string path, string contents)
    {
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, contents);
        File.Move(tempPath, path, overwrite: true);
    }
}
