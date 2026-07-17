namespace AgentMemory.Nams.Authentication;

/// <summary>
/// Supplies the bearer credential used to authenticate NAMS requests. Introduced instead of reading a static
/// token at every call so a future JWT/Auth0 path can slot in behind the same seam without changing
/// <see cref="Client.Neo4jNamsClientAdapter"/> (engineering plan §7 Phase 2, "Authentication and credential
/// lifecycle").
/// </summary>
internal interface INamsAccessTokenProvider
{
    /// <summary>Returns a currently-valid token, refreshing ahead of expiry if needed. Concurrent callers during a
    /// refresh must observe a single underlying refresh, not one each.</summary>
    ValueTask<NamsAccessToken> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Signals that the given token was rejected by the server (a 401) so the provider discards it and
    /// the next <see cref="GetTokenAsync"/> call fetches a fresh one. Never called for a 403 -- an authorization
    /// failure is not evidence the token itself is invalid.</summary>
    ValueTask InvalidateAsync(string rejectedTokenFingerprint, CancellationToken cancellationToken = default);
}
