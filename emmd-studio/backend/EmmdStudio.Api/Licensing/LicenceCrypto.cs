using System.Security.Cryptography;
using System.Text.Json;

namespace EmmdStudio.Api.Licensing;

/// <summary>
/// Offline licence signing/verification using ECDSA P-256 + SHA-256.
///
/// The seller keeps the PRIVATE key secret (used only when issuing licences).
/// The distributable engine ships the PUBLIC key and verifies licences with it —
/// no network call, so it works fully air-gapped. Without the private key a
/// customer cannot forge or extend a licence.
///
/// Token format (a single distributable string, also fine as a *.lic file):
///   GPLIC1.&lt;base64url(payload-json)&gt;.&lt;base64url(signature)&gt;
/// </summary>
public static class LicenceCrypto
{
    public const string TokenPrefix = "GPLIC1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Deterministic output so the same payload always signs/verifies identically.
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    /// <summary>Generate a fresh signing keypair. Run ONCE; store the private key as a secret.</summary>
    public static (string PrivateKeyPem, string PublicKeyPem) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), ecdsa.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>Sign a licence payload with the seller's private key, producing a distributable token.</summary>
    public static string Sign(OnPremLicencePayload payload, string privateKeyPem)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPem))
            throw new InvalidOperationException("No signing private key configured.");

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var signature = ecdsa.SignData(json, HashAlgorithmName.SHA256);

        return $"{TokenPrefix}.{Base64Url.Encode(json)}.{Base64Url.Encode(signature)}";
    }

    /// <summary>
    /// Verify a licence token against the public key and check expiry.
    /// Returns false (with a human-readable reason) for forged, corrupted, or expired licences.
    /// </summary>
    public static bool TryVerify(string token, string publicKeyPem, out OnPremLicencePayload? payload, out string error)
    {
        payload = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "No licence token provided.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            error = "No verification public key configured.";
            return false;
        }

        var parts = token.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != TokenPrefix)
        {
            error = "Malformed licence token.";
            return false;
        }

        byte[] json, signature;
        try
        {
            json = Base64Url.Decode(parts[1]);
            signature = Base64Url.Decode(parts[2]);
        }
        catch
        {
            error = "Licence token is not valid base64url.";
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            if (!ecdsa.VerifyData(json, signature, HashAlgorithmName.SHA256))
            {
                error = "Signature invalid — licence is forged or corrupted.";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"Could not verify signature: {ex.Message}";
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<OnPremLicencePayload>(json, JsonOptions);
        }
        catch
        {
            error = "Licence payload could not be read.";
            return false;
        }

        if (payload is null)
        {
            error = "Licence payload was empty.";
            return false;
        }

        if (payload.ExpiresAt is { } expiry && expiry < DateTime.UtcNow)
        {
            error = $"Licence expired on {expiry:yyyy-MM-dd}.";
            return false;
        }

        return true;
    }
}

internal static class Base64Url
{
    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
