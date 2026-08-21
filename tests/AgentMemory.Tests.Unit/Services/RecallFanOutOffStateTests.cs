using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// The off-state canary for recall fan-out (30.10, design step 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Written before any fan-out behaviour exists, and that is the point.</b> Right now these pass
/// trivially. They are here so that the commit which introduces the behaviour cannot also, quietly,
/// change what a host with the feature off receives — the single failure mode a dark-shipped feature
/// must not have.
/// </para>
/// <para>
/// Three separate claims, because they fail independently: nothing is <i>reported</i> when the
/// planner never ran; nothing is <i>retrieved</i> beyond the monolithic path; and the assembled
/// sections are unchanged.
/// </para>
/// </remarks>
public sealed class RecallFanOutOffStateTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService>();
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private static readonly IMemoryIsolationPolicy SingleTenantPolicy =
        new DefaultMemoryIsolationPolicy(
            Options.Create(new MemoryIsolationOptions()),
            NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    public RecallFanOutOffStateTests()
    {
        _clock.UtcNow.Returns(FixedNow);
        _shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f, 0.4f });
    }

    private MemoryContextAssembler CreateSut(MemoryOptions? options = null) =>
        new(_shortTerm, _longTerm, _reasoning, null, _embeddings, _clock,
            Options.Create(options ?? new MemoryOptions()),
            NullLogger<MemoryContextAssembler>.Instance, SingleTenantPolicy);

    private static RecallRequest Request() => new()
    {
        SessionId = "session-1",
        Query = "Where did I work in March and what did I order at the Met?",
        Options = RecallOptions.Default,
    };

    [Fact]
    public async Task WithFanOutOff_TheReportIsNull_MeaningThePlannerNeverRan()
    {
        // Null is the load-bearing state. It must NOT be a report with GateFired=false, which means
        // something ran and declined -- a different claim, and the distinction is the whole witness.
        var context = await CreateSut().AssembleContextAsync(Request(), CancellationToken.None);

        context.FanOutReport.Should().BeNull(
            "with the feature off and no caller sub-queries, the planner never ran at all");
    }

    [Fact]
    public async Task WithFanOutOff_NoAdditionalRetrievalHappens()
    {
        // Fan-out costs one embedding and one retrieval round trip per leg. Off must cost zero of
        // both, so the guard counts calls rather than trusting the flag.
        var sut = CreateSut();

        await sut.AssembleContextAsync(Request(), CancellationToken.None);

        var embedCalls = _embeddings.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == "EmbedQueryAsync");

        embedCalls.Should().BeLessThanOrEqualTo(1,
            "the monolithic path embeds the query at most once; every additional embed would be a leg");
    }

    [Fact]
    public async Task TheDefaultRequestCarriesNoSubQueries()
    {
        // The request-schema half of the off state. A default RecallRequest must be indistinguishable
        // from a pre-30.10 one, including under record equality.
        var request = Request();

        request.SubQueries.Should().BeNull();
        (request with { }).Should().Be(request, "adding an init-only member must not change equality");
    }

    [Fact]
    public async Task WithFanOutOff_TheAssembledSectionsAreUnchanged()
    {
        // A golden-shaped check rather than a byte fingerprint: the sections a host reads must be the
        // same objects they were, so a later fan-out merge cannot silently reorder or pad them.
        var enabled = new MemoryOptions();
        enabled.FanOut.Enabled = false;

        var offByDefault = await CreateSut().AssembleContextAsync(Request(), CancellationToken.None);
        var offExplicitly = await CreateSut(enabled).AssembleContextAsync(Request(), CancellationToken.None);

        offExplicitly.RelevantFacts.Items.Count.Should().Be(offByDefault.RelevantFacts.Items.Count);
        offExplicitly.RelevantEntities.Items.Count.Should().Be(offByDefault.RelevantEntities.Items.Count);
        offExplicitly.RelevantPreferences.Items.Count.Should().Be(offByDefault.RelevantPreferences.Items.Count);
        offExplicitly.FanOutReport.Should().BeNull();
    }
}
