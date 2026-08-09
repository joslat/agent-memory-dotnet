using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Retrieval;
using AgentMemory.Neo4j.Services;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.GraphRagAdapter;

/// <summary>
/// K7. Owner isolation on a retrieval path that has never been exercised end to end.
/// </summary>
/// <remarks>
/// GraphRAG has carried a recall budget of zero in every quality measurement, and a path nothing ever
/// runs is where an isolation gap survives longest. Two specifics make it worth checking rather than
/// assuming: <c>IMemoryIsolationPolicy</c> is an <b>optional</b> dependency here, so a composition
/// registering the Neo4j package without Core has no policy at all; and an unscoped owner produces a
/// query that is byte-for-byte the legacy unscoped one, which returns every owner's nodes.
/// <para>
/// The load-bearing property is therefore that StrictMultiTenant <b>throws</b> rather than quietly
/// falling back to an unscoped query — and that the throw is not swallowed by the source's
/// best-effort error handling, which deliberately converts genuine retrieval failures into empty
/// results.
/// </para>
/// </remarks>
public sealed class GraphRagOwnerIsolationTests
{
    private readonly IRetriever _retriever = Substitute.For<IRetriever>();

    public GraphRagOwnerIsolationTests() =>
        _retriever.SearchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new RetrieverResult([]));

    [Fact]
    public async Task TheRequestingOwnerReachesTheRetriever()
    {
        await CreateSut(Strict()).GetContextAsync(Request("owner-a")).ConfigureAwait(true);

        await _retriever.Received(1).SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), "owner-a", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneOwnerIdIsNeverSubstitutedForAnother()
    {
        await CreateSut(Strict()).GetContextAsync(Request("owner-b")).ConfigureAwait(true);

        await _retriever.DidNotReceive().SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), "owner-a", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StrictMultiTenantFailsClosedOnAnUnscopedRead()
    {
        // The property that matters most: an unscoped tenant read must throw, not silently become a
        // query that returns every owner's nodes.
        var act = () => CreateSut(Strict()).GetContextAsync(Request(userId: null));

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task TheIsolationFailureIsNotSwallowedIntoAnEmptyResult()
    {
        // The source deliberately converts retrieval failures into a best-effort empty result. An
        // isolation violation must not be laundered through that path: returning nothing would look
        // identical to a legitimate miss, and the caller would never learn it was unscoped.
        var act = () => CreateSut(Strict()).GetContextAsync(Request(userId: null));

        await act.Should().NotThrowAsync<InvalidOperationException>().ConfigureAwait(true);
        await _retriever.DidNotReceive().SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private Neo4jGraphRagContextSource CreateSut(IMemoryIsolationPolicy? policy) =>
        new(_retriever,
            new GraphRagOptions { IndexName = "idx", TopK = 5 },
            NullLogger<Neo4jGraphRagContextSource>.Instance,
            policy);

    private static IMemoryIsolationPolicy Strict() =>
        new DefaultMemoryIsolationPolicy(
            Options.Create(new MemoryIsolationOptions { Mode = MemoryIsolationMode.StrictMultiTenant }),
            NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    private static GraphRagContextRequest Request(string? userId) => new()
    {
        SessionId = "s",
        Query = "q",
        UserId = userId
    };
}
