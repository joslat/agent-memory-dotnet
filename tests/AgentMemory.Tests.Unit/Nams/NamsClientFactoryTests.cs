using FluentAssertions;
using AgentMemory.Nams;
using AgentMemory.Nams.Client;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsClientFactoryTests
{
    [Fact]
    public void NormalizeBaseAddress_NoTrailingSlash_AddsOne()
    {
        var normalized = NamsClientFactory.NormalizeBaseAddress(new Uri("https://memory.neo4jlabs.com/v1"));

        normalized.OriginalString.Should().Be("https://memory.neo4jlabs.com/v1/");
    }

    [Fact]
    public void NormalizeBaseAddress_AlreadyHasTrailingSlash_Unchanged()
    {
        var endpoint = new Uri("https://memory.neo4jlabs.com/v1/");

        var normalized = NamsClientFactory.NormalizeBaseAddress(endpoint);

        normalized.Should().Be(endpoint);
    }

    [Fact]
    public void NormalizeBaseAddress_CombinedWithRelativePath_PreservesVersionSegment()
    {
        // This is the actual bug the normalization guards against: a base URI without a trailing '/' drops its
        // last path segment when combined with a relative Uri.
        var normalized = NamsClientFactory.NormalizeBaseAddress(new Uri("https://memory.neo4jlabs.com/v1"));

        var combined = new Uri(normalized, "conversations");

        combined.Should().Be(new Uri("https://memory.neo4jlabs.com/v1/conversations"));
    }

    [Fact]
    public void ConfigureHttpClient_SetsBaseAddressAndTimeout()
    {
        using var client = new HttpClient();
        var options = new NamsOptions
        {
            Endpoint = new Uri("https://memory.neo4jlabs.com/v1"),
            ApiKey = "nams_key",
            RequestTimeout = TimeSpan.FromSeconds(7)
        };

        NamsClientFactory.ConfigureHttpClient(client, options);

        client.BaseAddress.Should().Be(new Uri("https://memory.neo4jlabs.com/v1/"));
        client.Timeout.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public void ConfigureHttpClient_WorkspaceIdSet_AddsWorkspaceHeader()
    {
        using var client = new HttpClient();
        var options = new NamsOptions
        {
            Endpoint = new Uri("https://memory.neo4jlabs.com/v1"),
            ApiKey = "nams_key",
            WorkspaceId = "a3c6679c-31a9-4035-95d9-7dfae2349cb5"
        };

        NamsClientFactory.ConfigureHttpClient(client, options);

        client.DefaultRequestHeaders.GetValues("X-Workspace-Id").Should().ContainSingle()
            .Which.Should().Be("a3c6679c-31a9-4035-95d9-7dfae2349cb5");
    }

    [Fact]
    public void ConfigureHttpClient_NoWorkspaceId_OmitsWorkspaceHeader()
    {
        using var client = new HttpClient();
        var options = new NamsOptions
        {
            Endpoint = new Uri("https://memory.neo4jlabs.com/v1"),
            ApiKey = "nams_key"
        };

        NamsClientFactory.ConfigureHttpClient(client, options);

        client.DefaultRequestHeaders.Contains("X-Workspace-Id").Should().BeFalse();
    }
}
