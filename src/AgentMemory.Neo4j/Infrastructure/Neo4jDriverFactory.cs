using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

internal sealed class Neo4jDriverFactory : INeo4jDriverFactory
{
    private readonly IDriver _driver;
    private readonly ILogger<Neo4jDriverFactory> _logger;

    public Neo4jDriverFactory(IOptions<Neo4jOptions> options, ILogger<Neo4jDriverFactory> logger)
    {
        _logger = logger;
        var cfg = options.Value;

        // A scheme with "+s" or "+ssc" (neo4j+s://, the address Aura gives, or bolt+s://) carries its own encryption and
        // trust, and the driver refuses any encryption setting beside it: the option speaks only for plain schemes.
        var schemeEncrypts = SchemeCarriesEncryption(cfg.Uri);

        _driver = GraphDatabase.Driver(
            cfg.Uri,
            AuthTokens.Basic(cfg.Username, cfg.Password),
            c =>
            {
                if (!schemeEncrypts)
                    c.WithEncryptionLevel(cfg.EncryptionEnabled ? EncryptionLevel.Encrypted : EncryptionLevel.None);
                c.WithMaxConnectionPoolSize(cfg.MaxConnectionPoolSize)
                    .WithConnectionAcquisitionTimeout(cfg.ConnectionAcquisitionTimeout);
                // Both unset by default: the driver keeps its own defaults, configured exactly as before.
                if (cfg.ConnectionTimeout is { } connectionTimeout) c.WithConnectionTimeout(connectionTimeout);
                if (cfg.MaxTransactionRetryTime is { } retryTime) c.WithMaxTransactionRetryTime(retryTime);
                // DRT-4: a connection quiet for long is tested before use; an idle one is closed only when asked.
                if (cfg.ConnectionLivenessCheckTimeout is { } liveness) c.WithConnectionLivenessCheckTimeout(liveness);
                if (cfg.ConnectionIdleTimeout is { } idle) c.WithConnectionIdleTimeout(idle);
            });

        _logger.LogInformation("Neo4j driver created. URI={Uri}, Database={Database}", cfg.Uri, cfg.Database);
    }

    public IDriver GetDriver() => _driver;

    /// <summary>Whether the URI's scheme sets encryption itself: neo4j+s, neo4j+ssc, bolt+s or bolt+ssc.</summary>
    internal static bool SchemeCarriesEncryption(string uri)
    {
        var colon = uri.IndexOf("://", StringComparison.Ordinal);
        return colon > 0 && uri.AsSpan(0, colon).Contains('+');
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposing Neo4j driver.");
        await _driver.DisposeAsync().ConfigureAwait(false);
    }
}
