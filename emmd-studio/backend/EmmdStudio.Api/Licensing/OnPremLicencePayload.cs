namespace EmmdStudio.Api.Licensing;

/// <summary>
/// The signed claims that travel inside an on-prem licence token. This is the
/// document the seller signs with their private key and the self-hosted engine
/// verifies offline with the embedded public key. Keep it small and stable —
/// changing a property name changes the signed bytes and invalidates old tokens.
/// </summary>
public record OnPremLicencePayload
{
    /// <summary>Public, human-quotable id (e.g. lic_ab12…). Also used for tracking/revocation.</summary>
    public string LicenceId { get; init; } = string.Empty;

    /// <summary>What the licence unlocks. Lets one keypair sign licences for several products later.</summary>
    public string Product { get; init; } = "genprompt-pdf-engine";

    /// <summary>Customer / organisation name the licence is issued to.</summary>
    public string Customer { get; init; } = string.Empty;

    public string ContactEmail { get; init; } = string.Empty;

    /// <summary>single-server | multi-instance | white-label</summary>
    public string Tier { get; init; } = "single-server";

    /// <summary>Max concurrent deployments allowed. 0 = unlimited (multi-instance / white-label).</summary>
    public int MaxInstances { get; init; } = 1;

    /// <summary>Feature flags the engine can check (e.g. "white-label", "all-tools").</summary>
    public string[] Features { get; init; } = System.Array.Empty<string>();

    public DateTime IssuedAt { get; init; }

    /// <summary>Term end. null = perpetual (rare; usually a year).</summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>Payload schema version, in case the format evolves.</summary>
    public int Version { get; init; } = 1;
}
