using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// G-15: general knowledge taught for everyone is stored shared, resolved against shared entities only, and
/// written on the administrative path even under strict multi-tenant isolation.
/// </summary>
public sealed class SharedKnowledgeExtractionTests
{
    private readonly IExtractionStage _stage = Substitute.For<IExtractionStage>();
    private readonly IPersistenceStage _persistence = Substitute.For<IPersistenceStage>();

    public SharedKnowledgeExtractionTests()
    {
        _stage.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<ExtractionTypes>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionStageResult());
        _persistence.PersistAsync(Arg.Any<ExtractionStageResult>(), Arg.Any<string?>(), Arg.Any<MemoryTrustLevel>(), Arg.Any<CancellationToken>())
            .Returns(new PersistenceResult());
    }

    private MemoryExtractionPipeline Pipeline(MemoryIsolationMode mode) => new(
        _stage, _persistence, NullLogger<MemoryExtractionPipeline>.Instance,
        new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions { Mode = mode }), NullLogger<DefaultMemoryIsolationPolicy>.Instance),
        Options.Create(new ExtractionOptions()), []);

    private static ExtractionRequest Request(bool shared, string? user) => new()
    {
        SessionId = "doc-1",
        UserId = user,
        ShareWithEveryone = shared,
        Messages =
        [
            new Message
            {
                MessageId = "chunk-1", SessionId = "doc-1", ConversationId = "doc-1", Role = "user",
                Content = "Alice fell down a very deep well.", TimestampUtc = DateTimeOffset.UnixEpoch,
            },
        ],
    };

    [Fact]
    public async Task Shared_knowledge_resolves_against_shared_rows_only_and_belongs_to_no_one()
    {
        await Pipeline(MemoryIsolationMode.StrictMultiTenant).ExtractAsync(Request(shared: true, user: null));

        await _stage.Received(1).ExtractAsync(
            Arg.Any<IReadOnlyList<Message>>(), Arg.Any<ExtractionTypes>(),
            Arg.Is<MemoryScope?>(s => s!.OwnerId == MemoryExtractionPipeline.SharedResolutionOwner && s.IncludeShared),
            Arg.Any<CancellationToken>());
        await _persistence.Received(1).PersistAsync(
            Arg.Any<ExtractionStageResult>(), (string?)null, Arg.Any<MemoryTrustLevel>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_the_flag_strict_isolation_still_refuses_an_owner_less_write()
    {
        var act = () => Pipeline(MemoryIsolationMode.StrictMultiTenant).ExtractAsync(Request(shared: false, user: null));

        await act.Should().ThrowAsync<AgentMemory.Abstractions.Exceptions.MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task Shared_and_a_user_together_are_refused()
    {
        var act = () => Pipeline(MemoryIsolationMode.StrictMultiTenant).ExtractAsync(Request(shared: true, user: "owner-1"));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_personal_request_is_unchanged()
    {
        await Pipeline(MemoryIsolationMode.StrictMultiTenant).ExtractAsync(Request(shared: false, user: "owner-1"));

        await _stage.Received(1).ExtractAsync(
            Arg.Any<IReadOnlyList<Message>>(), Arg.Any<ExtractionTypes>(),
            Arg.Is<MemoryScope?>(s => s!.OwnerId == "owner-1"), Arg.Any<CancellationToken>());
        await _persistence.Received(1).PersistAsync(
            Arg.Any<ExtractionStageResult>(), "owner-1", Arg.Any<MemoryTrustLevel>(), Arg.Any<CancellationToken>());
    }
}
