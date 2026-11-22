using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// A URI whose scheme sets encryption (neo4j+s://, the address Aura gives, or bolt+s://) builds a driver: the factory
/// used to add its own encryption setting beside the scheme's, which the driver refuses ("The encryption and trust
/// settings cannot both be set via uri scheme and driver configuration"). Found by the native gate's TLS tier
/// (NATIVE-PLAT-STORE). Building the driver does not connect.
/// </summary>
public sealed class Neo4jDriverFactorySchemeTests
{
    private static Neo4jDriverFactory Factory(string uri, bool encryptionEnabled) =>
        new(Options.Create(new Neo4jOptions { Uri = uri, EncryptionEnabled = encryptionEnabled }), NullLogger<Neo4jDriverFactory>.Instance);

    [Theory]
    [InlineData("neo4j+s://xxxx.databases.neo4j.io", false)]
    [InlineData("neo4j+s://xxxx.databases.neo4j.io", true)]
    [InlineData("bolt+s://localhost:7687", false)]
    [InlineData("neo4j+ssc://localhost:7687", false)]
    [InlineData("bolt+ssc://localhost:7687", true)]
    public async Task A_scheme_that_encrypts_builds_an_encrypted_driver_whatever_the_option_says(string uri, bool encryptionEnabled)
    {
        await using var factory = Factory(uri, encryptionEnabled);

        factory.GetDriver().Encrypted.Should().BeTrue();
    }

    [Theory]
    [InlineData("bolt://localhost:7687", false, false)]
    [InlineData("bolt://localhost:7687", true, true)]
    [InlineData("neo4j://localhost:7687", true, true)]
    public async Task A_plain_scheme_is_encrypted_by_the_option(string uri, bool encryptionEnabled, bool encrypted)
    {
        await using var factory = Factory(uri, encryptionEnabled);

        factory.GetDriver().Encrypted.Should().Be(encrypted);
    }

    [Theory]
    [InlineData("neo4j+s://host", true)]
    [InlineData("BOLT+SSC://host:7687", true)]
    [InlineData("bolt://host:7687", false)]
    [InlineData("neo4j://host", false)]
    [InlineData("bolt://user+tag@host:7687", false)]
    public void The_scheme_alone_decides_whether_it_carries_encryption(string uri, bool carries)
    {
        Neo4jDriverFactory.SchemeCarriesEncryption(uri).Should().Be(carries);
    }
}
