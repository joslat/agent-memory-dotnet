using System.Reflection;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// DRT-4 (C-08): a pooled connection quiet for two minutes is tested before it is used, so the first call after a pause
/// does not fail on a connection a firewall dropped; an idle timeout is set only when a host asks. Building the driver
/// does not connect.
/// </summary>
public sealed class Neo4jDriverFactoryConnectionTests
{
    private static Config ConfigOf(IDriver driver) =>
        (Config)driver.GetType().GetProperty("Config", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(driver)!;

    [Fact]
    public void A_connection_quiet_for_two_minutes_is_tested_and_none_is_closed_for_being_idle_by_default()
    {
        var options = new Neo4jOptions();

        options.ConnectionLivenessCheckTimeout.Should().Be(TimeSpan.FromMinutes(2));
        options.ConnectionIdleTimeout.Should().BeNull();
    }

    [Fact]
    public async Task The_settings_reach_the_driver()
    {
        await using var factory = new Neo4jDriverFactory(Options.Create(new Neo4jOptions
        {
            Uri = "bolt://localhost:7687", ConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        }), NullLogger<Neo4jDriverFactory>.Instance);

        var config = ConfigOf(factory.GetDriver());

        config.ConnectionLivenessThreshold.Should().Be(TimeSpan.FromMinutes(2));
        config.ConnectionIdleTimeout.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Null_leaves_the_drivers_own_default()
    {
        await using var factory = new Neo4jDriverFactory(Options.Create(new Neo4jOptions
        {
            Uri = "bolt://localhost:7687", ConnectionLivenessCheckTimeout = null,
        }), NullLogger<Neo4jDriverFactory>.Instance);

        ConfigOf(factory.GetDriver()).ConnectionLivenessThreshold.Should().Be(new Config().ConnectionLivenessThreshold);
    }
}
