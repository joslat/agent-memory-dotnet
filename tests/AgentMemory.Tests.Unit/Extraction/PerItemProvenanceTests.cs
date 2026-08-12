using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using AgentMemory.Extraction.Llm;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Per-item provenance (L3c): a stored fact must be attributable to the turn that stated it.
/// </summary>
/// <remarks>
/// <para>
/// <c>EXTRACTED_FROM</c> is written per ingestion batch — every item linked to every message the call
/// saw. On the evaluation corpus a fact links to a mean of <b>12</b> source messages and as many as
/// 30. The consequence is not merely imprecision: <b>any attribution metric derived from that edge is
/// satisfied by construction</b>. "Is the true source among this fact's linked messages?" is yes for
/// all thirty, so the metric cannot fail, and a provenance regression would be invisible to it.
/// </para>
/// <para>
/// So the assertions below are the ones that <i>can</i> fail — a narrowed link is asserted to name the
/// right turn, not merely to be short, and the fallbacks are asserted to keep the batch rather than
/// silently attribute a fact to whichever message happens to sit at a hallucinated index.
/// </para>
/// </remarks>
public sealed class PerItemProvenanceTests
{
    private readonly IEmbeddingOrchestrator _orchestrator = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IEntityRepository _entityRepo = Substitute.For<IEntityRepository>();
    private readonly IFactRepository _factRepo = Substitute.For<IFactRepository>();
    private readonly IPreferenceRepository _prefRepo = Substitute.For<IPreferenceRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGen = Substitute.For<IIdGenerator>();

    private readonly List<Fact> _writtenFacts = [];
    private readonly List<Preference> _writtenPreferences = [];
    private readonly List<(string Id, string MessageId)> _factEdges = [];

    private static readonly IReadOnlyList<string> FiveMessages =
        ["msg-1", "msg-2", "msg-3", "msg-4", "msg-5"];

    public PerItemProvenanceTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _idGen.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        _orchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[8]);

        _factRepo.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var fact = ci.Arg<Fact>();
                _writtenFacts.Add(fact);
                // A MERGE returns the STORED node, whose source ids are the union accumulated over
                // earlier ingestions. Returning a deliberately different list here is what catches an
                // implementation that writes edges from the repository result instead of the input.
                return Task.FromResult(fact with { SourceMessageIds = FiveMessages });
            });
        _factRepo.CreateExtractedFromRelationshipAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _factEdges.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1)));
                return Task.CompletedTask;
            });
        _prefRepo.UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _writtenPreferences.Add(ci.Arg<Preference>());
                return Task.FromResult(ci.Arg<Preference>());
            });
    }

    private PersistenceStage CreateSut() =>
        new(_orchestrator, _entityRepo, _factRepo, _prefRepo, _relRepo, _clock, _idGen,
            NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(), Options.Create(new ExtractionOptions()));

    private static ExtractionStageResult WithFact(int? sourceTurn) => new()
    {
        FilteredFacts =
        [
            new ExtractedFact
            {
                Subject = "user", Predicate = "lives in", Object = "Zurich",
                Confidence = 0.9, SourceTurn = sourceTurn,
            },
        ],
        SourceMessageIds = FiveMessages,
    };

    // ── the narrowing ─────────────────────────────────────────────────────

    [Fact]
    public async Task AReportedTurnNarrowsTheFactToThatOneMessage()
    {
        await CreateSut().PersistAsync(WithFact(sourceTurn: 3));

        // Turn N is messages[N-1]: the transcript is numbered from the same ordered list the source
        // ids come from, so this is a positional index rather than a search.
        _writtenFacts.Should().ContainSingle().Which.SourceMessageIds.Should().Equal(["msg-3"]);
    }

    [Fact]
    public async Task TheExtractedFromEdgeIsWrittenForThatMessageOnly()
    {
        // The stored property and the edge are two separate writes. Narrowing one while leaving the
        // other at batch breadth would leave the graph exactly as unattributable as before.
        await CreateSut().PersistAsync(WithFact(sourceTurn: 3));

        _factEdges.Select(edge => edge.MessageId).Should().Equal(["msg-3"]);
    }

    [Theory]
    [InlineData(1, "msg-1")]
    [InlineData(5, "msg-5")]
    public async Task TheBoundaryTurnsResolveToTheBoundaryMessages(int turn, string expected)
    {
        // Off-by-one here would attribute every fact to its neighbour, which reads as plausible
        // provenance forever after.
        await CreateSut().PersistAsync(WithFact(turn));

        _writtenFacts.Should().ContainSingle().Which.SourceMessageIds.Should().Equal([expected]);
    }

    // ── the fallbacks, which matter more ──────────────────────────────────

    [Fact]
    public async Task NoReportedTurnKeepsTheBatchLinksExactlyAsBefore()
    {
        // The byte-identical guarantee at defaults: ExtractionProvenanceMode.Batch never populates
        // SourceTurn, so every item takes this path and provenance is what it always was.
        await CreateSut().PersistAsync(WithFact(sourceTurn: null));

        _writtenFacts.Should().ContainSingle().Which.SourceMessageIds.Should().Equal(FiveMessages);
        _factEdges.Select(edge => edge.MessageId).Should().Equal(FiveMessages);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(int.MaxValue)]
    public async Task AnUnusableTurnFallsBackRatherThanAttributingWrongly(int turn)
    {
        // THE safety property. A resolved turn REPLACES the batch links, so a guessed number does not
        // add noise -- it discards the true source and substitutes a wrong one, and afterwards the
        // result is indistinguishable from precise attribution. Coarse provenance is recoverable;
        // confidently wrong provenance is not. Clamping to the nearest valid index would be the
        // tempting bug: it always produces an answer, and the answer is fabricated.
        await CreateSut().PersistAsync(WithFact(turn));

        _writtenFacts.Should().ContainSingle().Which.SourceMessageIds.Should().Equal(FiveMessages);
    }

    [Fact]
    public async Task EdgesComeFromTheInputItemNotTheMergedRepositoryResult()
    {
        // The fact repository here returns a stored node whose source ids are the full batch, mimicking
        // a MERGE against an earlier ingestion. Writing edges from that result would re-link this fact
        // to messages it was not extracted from -- restoring the exact breadth this removes, while the
        // stored property still looked correct.
        await CreateSut().PersistAsync(WithFact(sourceTurn: 2));

        _factEdges.Select(edge => edge.MessageId).Should().Equal(["msg-2"]);
    }

    [Fact]
    public async Task PreferencesNarrowToo()
    {
        var extraction = new ExtractionStageResult
        {
            FilteredPreferences =
            [
                new ExtractedPreference
                {
                    Category = "travel", PreferenceText = "aisle seats",
                    Confidence = 0.9, SourceTurn = 4,
                },
            ],
            SourceMessageIds = FiveMessages,
        };

        await CreateSut().PersistAsync(extraction);

        _writtenPreferences.Should().ContainSingle().Which.SourceMessageIds.Should().Equal(["msg-4"]);
    }

    // ── the resolver's own contract ───────────────────────────────────────

    [Fact]
    public void NarrowingIsReportedOnlyWhenItActuallyHappened()
    {
        // A resolver that never fires is indistinguishable from one that always does if the only
        // signal is "the fact has source ids". Single-message batches are not a narrowing: there was
        // nothing to narrow.
        SourceTurnProvenance.Narrowed(3, FiveMessages).Should().BeTrue();
        SourceTurnProvenance.Narrowed(null, FiveMessages).Should().BeFalse();
        SourceTurnProvenance.Narrowed(9, FiveMessages).Should().BeFalse();
        SourceTurnProvenance.Narrowed(1, ["msg-1"]).Should().BeFalse();
    }

    // ── the prompt and transcript, which must move together ───────────────

    [Fact]
    public void TheDefaultModeAddsNothingToThePrompt()
    {
        ExtractionPromptSemantics.ProvenanceInstruction(ExtractionProvenanceMode.Batch)
            .Should().BeEmpty();
    }

    [Fact]
    public void PerItemAsksForOneTurnAndForbidsGuessing()
    {
        var instruction = ExtractionPromptSemantics.ProvenanceInstruction(
            ExtractionProvenanceMode.PerItem);

        instruction.Should().Contain("source_turn");
        // Load-bearing: a resolved turn replaces the batch links, so an invented number is worse than
        // no number at all.
        instruction.Should().MatchRegex("(?i)(unsure|never guess)");
    }

    [Theory]
    [InlineData(ExtractionProvenanceMode.PerItem)]
    public void EveryExtractorRungCarriesTheInstruction(ExtractionProvenanceMode mode)
    {
        // The three rungs are meant to be interchangeable, and each rewrote its prompt from scratch.
        // A setting only some of them honour makes behaviour depend on a performance flag -- which is
        // how TemporalValidityMode ended up inert on the batch rung.
        var expected = ExtractionPromptSemantics.ProvenanceInstruction(mode);

        var prompts = new (string Rung, string Prompt)[]
        {
            ("per-kind fact", LlmFactExtractor.BuildSystemPrompt(
                AssistantContentMode.Ignore, TemporalValidityMode.Ignore, mode)),
            ("unified", LlmUnifiedMemoryExtractor.BuildSystemPrompt(
                AssistantContentMode.Ignore, [], TemporalValidityMode.Ignore, mode)),
            ("multi-session batch", LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(
                vocabulary: null, AssistantContentMode.Ignore, TemporalValidityMode.Ignore, mode)),
        };

        foreach (var (rung, prompt) in prompts)
            prompt.Should().Contain(expected, $"the {rung} rung must honour ExtractionProvenanceMode");
    }

    [Fact]
    public void NoExtractorRungChangesItsPromptAtTheDefault()
    {
        // Prompt bytes are fingerprinted into every measured run, and the batch rung additionally uses
        // its prompt for TOKEN ACCOUNTING -- an instruction it appends but does not count would make
        // the frozen batch plan under-estimate by exactly that text.
        var prompts = new[]
        {
            LlmFactExtractor.BuildSystemPrompt(
                AssistantContentMode.Ignore, TemporalValidityMode.Ignore, ExtractionProvenanceMode.Batch),
            LlmUnifiedMemoryExtractor.BuildSystemPrompt(
                AssistantContentMode.Ignore, [], TemporalValidityMode.Ignore, ExtractionProvenanceMode.Batch),
            LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(
                vocabulary: null, AssistantContentMode.Ignore, TemporalValidityMode.Ignore,
                ExtractionProvenanceMode.Batch),
        };

        foreach (var prompt in prompts)
            prompt.Should().NotContain("source_turn");
    }

    [Fact]
    public void TheDefaultTranscriptIsUnnumbered()
    {
        // Prompt AND transcript bytes are fingerprinted into every measured run. Numbering by default
        // would invalidate every sealed base without changing a single setting.
        var messages = Messages(2);

        ConversationTextBuilder.Build(messages).Should().Be("user: turn 1\nuser: turn 2");
    }

    [Fact]
    public void TheNumberedTranscriptIsOneBasedAndPositional()
    {
        // The numbering IS the contract the resolver indexes against; if these two ever disagreed,
        // every fact would be attributed to the wrong turn and nothing would report an error.
        ConversationTextBuilder.BuildNumbered(Messages(3))
            .Should().Be("[1] user: turn 1\n[2] user: turn 2\n[3] user: turn 3");
    }

    [Fact]
    public void TheNumberedTranscriptHandlesAnEmptyConversation()
    {
        ConversationTextBuilder.BuildNumbered([]).Should().BeEmpty();
    }

    private static IReadOnlyList<Message> Messages(int count) =>
        Enumerable.Range(1, count).Select(index => new Message
        {
            MessageId = $"msg-{index}",
            ConversationId = "c-1",
            SessionId = "s-1",
            Role = "user",
            Content = $"turn {index}",
            TimestampUtc = DateTimeOffset.UnixEpoch.AddMinutes(index),
        }).ToArray();
}
