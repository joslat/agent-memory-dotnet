namespace AgentMemory.Nams.Authentication;

/// <summary>
/// A credential handed to <see cref="NamsAuthenticationHandler"/> for the <c>Authorization: Bearer</c> header.
/// <see cref="ToString"/> deliberately never exposes <see cref="Value"/> -- only a low-cardinality fingerprint,
/// per the engineering plan's explicit "expose token age only as low-cardinality diagnostics, never the token or
/// raw fingerprint" requirement (a truncated hash is a reasonable middle ground: enough to correlate log lines
/// about "the same token", never enough to reconstruct or replay it).
/// </summary>
internal readonly struct NamsAccessToken
{
    public string Value { get; }

    /// <summary>Absolute expiry, if the token is time-limited. <c>null</c> for a non-expiring static API key.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    public NamsAccessToken(string value, DateTimeOffset? expiresAt = null)
    {
        Value = value;
        ExpiresAt = expiresAt;
    }

    /// <summary>A short, non-reversible fingerprint safe to include in logs/diagnostics.</summary>
    public string Fingerprint => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Value)))[..8];

    public override string ToString() => $"NamsAccessToken[{Fingerprint}]";
}
