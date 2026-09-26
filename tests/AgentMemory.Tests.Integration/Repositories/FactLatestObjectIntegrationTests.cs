using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// I-5 (review round 3): the user's name is read with one keyed query, whatever casing and separators the
/// extractor used, and only from a live fact of the owner.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class FactLatestObjectIntegrationTests : IAsyncLifetime
{
    private static readonly string[] Self = ["user", "the user", "i", "me", "myself"];
    private static readonly string[] Naming = ["is named", "is called"];

    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    public FactLatestObjectIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static Fact Naming_(string subject, string predicate, string name, string owner = "alice", DateTimeOffset? createdAt = null) => new()
    {
        FactId = Guid.NewGuid().ToString("N"),
        Subject = subject,
        Predicate = predicate,
        Object = name,
        Confidence = 1,
        CreatedAtUtc = createdAt ?? DateTimeOffset.UtcNow,
        OwnerId = owner,
    };

    private Task<string?> NameOf(string owner) =>
        _facts.FindLatestObjectAsync(Self, Naming, MemoryScope.For(owner, includeShared: false));

    [Fact]
    public async Task Casing_and_separators_the_extractor_used_still_match()
    {
        await _facts.UpsertAsync(Naming_("User", "is_named", "Dana"));

        (await NameOf("alice")).Should().Be("Dana");
    }

    [Fact]
    public async Task Another_owners_name_is_never_returned()
    {
        await _facts.UpsertAsync(Naming_("user", "is named", "Bob", owner: "bob"));

        (await NameOf("alice")).Should().BeNull();
    }

    [Fact]
    public async Task An_invalidated_or_expired_name_is_not_used()
    {
        var invalidated = await _facts.UpsertAsync(Naming_("user", "is named", "Dana"));
        await _facts.InvalidateAsync(invalidated.FactId, MemoryScope.For("alice", includeShared: false));
        await _facts.UpsertAsync(Naming_("user", "is called", "Dee") with { ValidUntil = DateTimeOffset.UtcNow.AddDays(-1) });

        (await NameOf("alice")).Should().BeNull();
    }

    [Fact]
    public async Task The_most_recently_stated_name_wins()
    {
        await _facts.UpsertAsync(Naming_("user", "is named", "Dana", createdAt: DateTimeOffset.UtcNow.AddDays(-2)));
        await _facts.UpsertAsync(Naming_("user", "is called", "Dee", createdAt: DateTimeOffset.UtcNow.AddDays(-1)));

        (await NameOf("alice")).Should().Be("Dee");
    }
}
