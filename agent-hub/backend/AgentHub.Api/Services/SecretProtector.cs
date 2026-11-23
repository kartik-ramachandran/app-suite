using System.Security.Cryptography;
using System.Text;

namespace AgentHub.Api.Services;

/// <summary>
/// AES-GCM encryption for third-party credentials at rest (MCP bearer tokens,
/// API keys, OAuth tokens). Authenticated encryption, so a tampered ciphertext
/// fails to decrypt rather than silently returning garbage.
///
/// Key resolution mirrors EmmdSealService:
///   Secrets:MasterKey    base64 32-byte key (set this in production)
///   Secrets:MasterKeyPath file to load/persist a generated key
/// A container restart with neither configured means previously stored secrets
/// can no longer be decrypted — connections then report needs_auth and the user
/// re-enters the credential, rather than the app failing in a confusing way.
/// </summary>
public class SecretProtector
{
    private readonly byte[] _key;
    private readonly ILogger<SecretProtector> _logger;
    private const string Prefix = "v1:";

    public SecretProtector(IConfiguration config, ILogger<SecretProtector> logger, IHostEnvironment env)
    {
        _logger = logger;

        var configured = config["Secrets:MasterKey"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var bytes = Convert.FromBase64String(configured);
                if (bytes.Length == 32) { _key = bytes; return; }
                _logger.LogWarning("Secrets:MasterKey must be 32 bytes base64 — falling back to key file.");
            }
            catch (FormatException)
            {
                _logger.LogWarning("Secrets:MasterKey is not valid base64 — falling back to key file.");
            }
        }

        var keyPath = config["Secrets:MasterKeyPath"];
        if (string.IsNullOrWhiteSpace(keyPath))
            keyPath = Path.Combine(env.ContentRootPath, "secrets-master.key");

        try
        {
            if (File.Exists(keyPath))
            {
                var bytes = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
                if (bytes.Length == 32) { _key = bytes; return; }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read secret key at {Path}; generating a new one.", keyPath);
        }

        _key = RandomNumberGenerator.GetBytes(32);
        try
        {
            File.WriteAllText(keyPath, Convert.ToBase64String(_key));
            _logger.LogInformation("Generated a new secrets master key at {Path}.", keyPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Generated an in-memory secrets key — stored credentials will not survive a restart. Set Secrets:MasterKey in production.");
        }
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);

        return Prefix + Convert.ToBase64String(nonce) + ":" +
               Convert.ToBase64String(cipher) + ":" + Convert.ToBase64String(tag);
    }

    /// <summary>Returns null when the value cannot be decrypted (rotated or lost key).</summary>
    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue) || !protectedValue.StartsWith(Prefix)) return null;
        try
        {
            var parts = protectedValue.Substring(Prefix.Length).Split(':');
            if (parts.Length != 3) return null;
            var nonce = Convert.FromBase64String(parts[0]);
            var cipher = Convert.FromBase64String(parts[1]);
            var tag = Convert.FromBase64String(parts[2]);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(_key, tag.Length);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null; // wrong/rotated key or tampered ciphertext
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
