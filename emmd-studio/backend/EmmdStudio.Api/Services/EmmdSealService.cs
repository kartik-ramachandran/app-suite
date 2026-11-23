using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmmdStudio.Api.Licensing;
using EmmdStudio.Api.Models;

namespace EmmdStudio.Api.Services;

/// <summary>
/// Platform-held integrity sealing for EMMD documents. GenPrompt keeps a single
/// ECDSA P-256 keypair and signs a SHA-256 hash of a document's canonical
/// content. Anyone with the public key can then verify that a sealed document
/// was produced by GenPrompt and has not been altered since.
///
/// This is TAMPER-EVIDENCE, not a legally-binding e-signature: it proves
/// "sealed by GenPrompt, content unmodified", nothing about a human's intent to
/// sign. The seal is transported inside the .emmd file as an &lt;!--emmd:seal--&gt;
/// comment, so a sealed document still opens as ordinary Markdown.
///
/// Key resolution (mirrors OnPremLicenceService):
///   Emmd:SealPrivateKeyPem / Emmd:SealPublicKeyPem   provide a fixed keypair
///   Emmd:SealKeyPath                                 file to load/persist the keypair
/// If none is configured, a keypair is generated once and persisted next to the
/// app (dev / self-host convenience) so seals stay verifiable across restarts.
/// Registered as a singleton so the key is stable for the process lifetime.
/// </summary>
public class EmmdSealService
{
    public const string TokenPrefix = "GPSEAL1";
    public const string Algorithm = "ES256"; // ECDSA P-256 + SHA-256

    private readonly ILogger<EmmdSealService> _logger;
    private readonly string _privateKeyPem;
    public string PublicKeyPem { get; }
    public string DefaultSealer { get; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public EmmdSealService(IConfiguration config, ILogger<EmmdSealService> logger, IHostEnvironment env)
    {
        _logger = logger;
        DefaultSealer = config["Emmd:SealerName"] ?? "GenPrompt Platform";

        var privatePem = config["Emmd:SealPrivateKeyPem"];
        var publicPem = config["Emmd:SealPublicKeyPem"];

        if (!string.IsNullOrWhiteSpace(privatePem) && !string.IsNullOrWhiteSpace(publicPem))
        {
            _privateKeyPem = privatePem!;
            PublicKeyPem = publicPem!;
            _logger.LogInformation("EMMD seal: using configured keypair.");
            return;
        }

        // No keypair in config — load from (or create at) a stable file so seals
        // remain verifiable across restarts in dev/self-host deployments.
        var keyPath = config["Emmd:SealKeyPath"];
        if (string.IsNullOrWhiteSpace(keyPath))
            keyPath = Path.Combine(env.ContentRootPath, "emmd-seal-key.pem");

        try
        {
            if (File.Exists(keyPath))
            {
                _privateKeyPem = File.ReadAllText(keyPath);
                using var ec = ECDsa.Create();
                ec.ImportFromPem(_privateKeyPem);
                PublicKeyPem = ec.ExportSubjectPublicKeyInfoPem();
                _logger.LogInformation("EMMD seal: loaded keypair from {Path}.", keyPath);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EMMD seal: could not load key at {Path}; generating a new one.", keyPath);
        }

        var (priv, pub) = LicenceCrypto.GenerateKeyPair();
        _privateKeyPem = priv;
        PublicKeyPem = pub;
        try
        {
            File.WriteAllText(keyPath, priv);
            _logger.LogWarning("EMMD seal: generated a NEW keypair and persisted it to {Path}. " +
                "For production, set Emmd:SealPrivateKeyPem / Emmd:SealPublicKeyPem from a secret.", keyPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EMMD seal: generated an EPHEMERAL keypair (could not persist to {Path}). " +
                "Seals will not verify after a restart until a fixed key is configured.", keyPath);
        }
    }

    /// <summary>
    /// Reduce content to the exact bytes covered by the seal. Must be identical
    /// on sign and verify: normalise line endings, strip any trailing seal
    /// comment, and trim trailing whitespace. Everything else is preserved.
    /// </summary>
    public static byte[] Canonicalize(string content)
    {
        var s = (content ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        s = StripSealComment(s);
        s = s.TrimEnd();
        return Encoding.UTF8.GetBytes(s);
    }

    public static string HashHex(string content) =>
        Convert.ToHexString(SHA256.HashData(Canonicalize(content))).ToLowerInvariant();

    private static string StripSealComment(string s)
    {
        // Remove a trailing <!--emmd:seal ... --> comment (and its surrounding
        // whitespace) so the seal never covers itself.
        var idx = s.LastIndexOf("<!--emmd:seal", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return s;
        var end = s.IndexOf("-->", idx, StringComparison.Ordinal);
        if (end < 0) return s.Substring(0, idx);
        // Only strip if nothing but whitespace follows the comment (it is a trailer).
        var after = s.Substring(end + 3);
        if (after.Trim().Length != 0) return s;
        return s.Substring(0, idx);
    }

    /// <summary>Seal a document's content, returning the seal token and hash.</summary>
    public (string Token, string Hash, string SealedBy, DateTime SealedAt) Seal(string docId, string content, string? sealedBy = null)
    {
        var hash = HashHex(content);
        var sealer = string.IsNullOrWhiteSpace(sealedBy) ? DefaultSealer : sealedBy!.Trim();
        var sealedAt = DateTime.UtcNow;

        var payload = new EmmdSealPayload
        {
            V = 1,
            DocId = docId,
            Hash = hash,
            Alg = Algorithm,
            SealedBy = sealer,
            SealedAt = sealedAt,
        };

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(_privateKeyPem);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var signature = ecdsa.SignData(json, HashAlgorithmName.SHA256);
        var token = $"{TokenPrefix}.{Base64Url.Encode(json)}.{Base64Url.Encode(signature)}";

        return (token, hash, sealer, sealedAt);
    }

    /// <summary>
    /// Verify a seal token against the current content. Reports whether the
    /// content is unsealed, validly sealed, or tampered (signature ok but the
    /// content hash no longer matches, or the signature itself is invalid).
    /// </summary>
    public EmmdSealStatusDto Verify(string? content, string? sealToken)
    {
        if (string.IsNullOrWhiteSpace(sealToken))
            return new EmmdSealStatusDto { Sealed = false, Valid = false, Tampered = false };

        var status = new EmmdSealStatusDto { Sealed = true };

        var parts = sealToken.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != TokenPrefix)
        {
            status.Tampered = true;
            status.Reason = "Malformed seal token.";
            return status;
        }

        byte[] json, signature;
        try
        {
            json = Base64Url.Decode(parts[1]);
            signature = Base64Url.Decode(parts[2]);
        }
        catch
        {
            status.Tampered = true;
            status.Reason = "Seal token is not valid base64url.";
            return status;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(PublicKeyPem);
            if (!ecdsa.VerifyData(json, signature, HashAlgorithmName.SHA256))
            {
                status.Tampered = true;
                status.Reason = "Seal signature is invalid — the seal was forged or corrupted.";
                return status;
            }
        }
        catch (Exception ex)
        {
            status.Tampered = true;
            status.Reason = $"Could not verify seal signature: {ex.Message}";
            return status;
        }

        EmmdSealPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<EmmdSealPayload>(json, JsonOptions);
        }
        catch
        {
            status.Tampered = true;
            status.Reason = "Seal payload could not be read.";
            return status;
        }
        if (payload is null)
        {
            status.Tampered = true;
            status.Reason = "Seal payload was empty.";
            return status;
        }

        status.SealedBy = payload.SealedBy;
        status.SealedAt = payload.SealedAt;

        var actualHash = HashHex(content ?? string.Empty);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualHash),
                Encoding.ASCII.GetBytes(payload.Hash ?? string.Empty)))
        {
            status.Tampered = true;
            status.Valid = false;
            status.Reason = "Content has changed since it was sealed.";
            return status;
        }

        status.Valid = true;
        status.Reason = "Sealed by " + payload.SealedBy + " — content unmodified.";
        return status;
    }
}

/// <summary>The signed portion of an EMMD seal.</summary>
public class EmmdSealPayload
{
    public int V { get; set; } = 1;
    public string DocId { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string Alg { get; set; } = EmmdSealService.Algorithm;
    public string SealedBy { get; set; } = string.Empty;
    public DateTime SealedAt { get; set; }
}
