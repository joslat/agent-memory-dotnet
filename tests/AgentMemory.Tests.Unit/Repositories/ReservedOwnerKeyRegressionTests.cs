using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

/// <summary>Exercise the real public write parameter path without a database or fabricated merged nodes.</summary>
public sealed class ReservedOwnerKeyRegressionTests
{
    private sealed class Captured : Exception;
    private static async Task<string> FactOwnerKey(string? owner)
    {
        string? captured = null;
        var runner = Substitute.For<IAsyncQueryRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object>>()).Returns(call =>
        {
            captured = global::Neo4j.Driver.ValueExtensions.As<string>(call.ArgAt<IDictionary<string, object>>(1)["ownerKey"]);
            return Task.FromException<IResultCursor>(new Captured());
        });
        var transactions = Substitute.For<INeo4jTransactionRunner>();
        transactions.WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<Fact>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<IAsyncQueryRunner, Task<Fact>>>()(runner));
        var repository = new Neo4jFactRepository(transactions, NullLogger<Neo4jFactRepository>.Instance);
        var write = () => repository.UpsertAsync(new Fact
        { FactId = "fact", OwnerId = owner, Subject = "Pepe", Predicate = "arrives", Object = "tomorrow", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch });
        await write.Should().ThrowAsync<Captured>();
        captured.Should().NotBeNull();
        return captured!;
    }

    [Theory]
    [InlineData("*")] [InlineData("**")] [InlineData("***")]
    public async Task Private_owner_keys_are_distinct_from_the_shared_bucket(string owner) =>
        (await FactOwnerKey(owner)).Should().NotBe(await FactOwnerKey(null));

    [Fact]
    public async Task Escaping_reserved_owner_keys_does_not_collapse_two_private_owners()
    {
        var keys = new List<string>();
        foreach (var owner in new[] { "*", "**", "***", "****", "pablito" }) keys.Add(await FactOwnerKey(owner));
        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Ordinary_owner_keys_keep_their_existing_storage_identity() =>
        (await FactOwnerKey("pablito")).Should().Be("pablito");
}
