using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Recall;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// The MAF provider must not embed a query no surviving retrieval will consume.
/// </summary>
/// <remarks>
/// <para>
/// The provider generates the embedding itself and hands it to the assembler, so the assembler's own
/// gate cannot help here: narrowing categories in a policy would <b>relocate</b> the provider's call
/// rather than remove it. This is the trap that made "task-aware recall saves the embedding" false as
/// originally designed, and it needs its own test on this side of the boundary.
/// </para>
/// <para>
/// Recent messages are session-scoped and time-ordered, so a policy narrowing a trivial turn down to
/// them alone is the case that matters: it must cost <b>no</b> provider round trip.
/// </para>
/// </remarks>
public sealed class ProviderEmbeddingElisionTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGenerator = Substitute.For<IIdGenerator>();

    public ProviderEmbeddingElisionTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _idGenerator.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[] { 0.1f }));
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RecallResult
            {
                Context = new MemoryContext { SessionId = "s1", AssembledAtUtc = DateTimeOffset.UtcNow }
            });
    }

    /// <summary>A policy that narrows a turn to recent messages only — no vector category survives.</summary>
    private sealed class RecentMessagesOnlyPolicy : IAutomaticRecallPolicy
    {
        public ValueTask<AutomaticRecallDecision> DecideAsync(
            AutomaticRecallContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AutomaticRecallDecision
            {
                ShouldRecall = true,
                Categories = AutomaticRecallCategories.RecentMessages,
            });
    }

    private Neo4jMemoryContextProvider CreateSut(IAutomaticRecallPolicy? policy = null) =>
        new(_memoryService,
            _embeddings,
            _clock,
            _idGenerator,
            Options.Create(new MemoryOptions()),
            Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance,
            recallPolicy: policy);

    [Fact]
    public async Task NarrowingToRecentMessagesOnlyCostsNoEmbeddingCall()
    {
        var sut = CreateSut(new RecentMessagesOnlyPolicy());

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "ok") }, "s1", "c1", CancellationToken.None);

        await _embeddings.DidNotReceive()
            .EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecallStillHappensWhenTheEmbeddingIsElided()
    {
        // The saving must be the embedding, NOT the recall: recent messages are still wanted, and a
        // provider that quietly stopped recalling would look identical on the embedding assertion above.
        var sut = CreateSut(new RecentMessagesOnlyPolicy());

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "ok") }, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r =>
                r.QueryEmbedding == null &&
                r.Options!.MaxRecentMessages > 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheDefaultPolicyStillEmbedsExactlyOnce()
    {
        // The byte-identical guarantee: ConfiguredAutomaticRecallPolicy uses Categories.All, so every
        // vector category survives and the call happens exactly as it did before.
        var sut = CreateSut();

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "what did we decide about pricing?") },
            "s1", "c1", CancellationToken.None);

        await _embeddings.Received(1)
            .EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
