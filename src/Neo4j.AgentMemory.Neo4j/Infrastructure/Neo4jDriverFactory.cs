using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

public sealed class Neo4jDriverFactory : INeo4jDriverFactory
{
    private readonly IDriver _driver;
    private readonly ILogger<Neo4jDriverFactory> _logger;

    public Neo4jDriverFactory(IOptions<Neo4jOptions> options, ILogger<Neo4jDriverFactory> logger)
    {
        _logger = logger;
        var cfg = options.Value;

        var encryptionLevel = cfg.EncryptionEnabled
            ? EncryptionLevel.Encrypted
            : EncryptionLevel.None;

        _driver = GraphDatabase.Driver(
            cfg.Uri,
            AuthTokens.Basic(cfg.Username, cfg.Password),
            c => c
                .WithEncryptionLevel(encryptionLevel)
                .WithMaxConnectionPoolSize(cfg.MaxConnectionPoolSize)
                .WithConnectionAcquisitionTimeout(cfg.ConnectionAcquisitionTimeout));

        _logger.LogInformation("Neo4j driver created. URI={Uri}, Database={Database}", cfg.Uri, cfg.Database);
    }

    public IDriver GetDriver() => _driver;

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Disposing Neo4j driver.");
        await _driver.DisposeAsync();
    }
}
