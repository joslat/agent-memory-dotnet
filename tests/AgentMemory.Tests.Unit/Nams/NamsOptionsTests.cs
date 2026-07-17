using FluentAssertions;
using AgentMemory.Nams;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsOptionsTests
{
    [Fact]
    public void Defaults_MatchExpectedValues()
    {
        var options = new NamsOptions { Endpoint = new Uri("https://memory.neo4jlabs.com/v1") };

        options.ApiKey.Should().BeNull();
        options.WorkspaceId.Should().BeNull();
        options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.MaxRetryAttempts.Should().Be(3);
        options.InitialRetryDelay.Should().Be(TimeSpan.FromMilliseconds(250));
        options.PersistenceFailureMode.Should().Be(NamsPersistenceFailureMode.BestEffort);
        options.AllowInsecureEndpointForLocalDevelopment.Should().BeFalse();
    }

    // ── Stabilization-style secret-hygiene guard: NamsOptions is deliberately a plain class, not a
    // record, so its inherited object.ToString() never dumps ApiKey. Regression-guards that design
    // decision rather than trusting it silently. ──

    [Fact]
    public void ToString_DoesNotContainApiKey()
    {
        var options = new NamsOptions
        {
            Endpoint = new Uri("https://memory.neo4jlabs.com/v1"),
            ApiKey = "nams_super_secret_value_12345"
        };

        options.ToString().Should().NotContain("nams_super_secret_value_12345");
    }

    [Fact]
    public void GetType_IsNotARecord()
    {
        // A record type carries a compiler-generated <Clone>$ method; NamsOptions must not have one,
        // since that's precisely what would make ToString() start printing ApiKey.
        typeof(NamsOptions).GetMethods()
            .Should().NotContain(m => m.Name == "<Clone>$",
                "NamsOptions must stay a plain class -- a record's compiler-generated ToString() would print ApiKey");
    }
}
