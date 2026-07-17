using Microsoft.Extensions.Options;

namespace AgentMemory.Nams.Authentication;

/// <summary>
/// The only <see cref="INamsAccessTokenProvider"/> implementation registered in this phase -- wraps
/// <see cref="NamsOptions.ApiKey"/> as a non-expiring token. JWT/Auth0 support is deferred (its refresh/expiry
/// contract is unconfirmed -- see <c>strategy/NAMS/Neo4j_Questions.md</c> #30); the engineering plan itself says
/// to prefer the raw API key path when refresh semantics are unclear.
/// </summary>
internal sealed class StaticApiKeyNamsAccessTokenProvider : INamsAccessTokenProvider
{
    private readonly NamsAccessToken _token;

    public StaticApiKeyNamsAccessTokenProvider(IOptions<NamsOptions> options)
    {
        // NamsOptionValidator.HasApiKey (ValidateOnStart) guarantees this is non-null/non-whitespace before this
        // type is ever constructed.
        _token = new NamsAccessToken(options.Value.ApiKey!);
    }

    public ValueTask<NamsAccessToken> GetTokenAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_token);

    /// <summary>No-op: a static API key cannot be refreshed. A 401 against this provider surfaces as a genuine,
    /// non-retryable authentication failure after the single bounded retry <see cref="NamsAuthenticationHandler"/>
    /// always attempts.</summary>
    public ValueTask InvalidateAsync(string rejectedTokenFingerprint, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
