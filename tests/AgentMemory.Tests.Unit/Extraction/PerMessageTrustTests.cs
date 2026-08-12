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
/// The falsifier for per-message trust: a batch containing both a user's claim and the model's own must
/// not record them identically.
/// </summary>
/// <remarks>
/// <para>
/// Trust was stamped once per extraction request and applied to every item in it. That is invisible
/// while <c>AssistantContentMode.Ignore</c> ships, because nothing assistant-derived is extracted at
/// all — and it becomes a defect the instant the mode is switched on, at which point every claim the
/// model made about the world is stored with the same label as the ones the user typed.
/// </para>
/// <para>
/// The failure is <b>silent and permanent</b>: nothing errors, the graph looks correct, and the
/// distinction the enum exists to draw is gone by the time anyone queries for it. So this asserts the
/// <i>distribution</i>, not just that a mapping function returns the right value — a correct mapping
/// that is never reached would satisfy the latter.
/// </para>
/// <para>
/// Our own NAMS subsystem has always mapped <c>"assistant" =&gt; ModelGenerated</c> correctly. One
/// subsystem getting it right while the other could not express it is what made this worth closing.
/// </para>
/// </remarks>
public sealed class PerMessageTrustTests
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

    public PerMessageTrustTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _idGen.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        _orchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[8]);

        _factRepo.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _writtenFacts.Add(ci.Arg<Fact>());
                return Task.FromResult(ci.Arg<Fact>());
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

    private static ExtractionStageResult WithFacts(params ExtractedFact[] facts) =>
        new() { FilteredFacts = facts };

    private static ExtractedFact Fact(string @object, string? sourceRole) => new()
    {
        Subject = "user",
        Predicate = "mentioned",
        Object = @object,
        Confidence = 0.9,
        SourceRole = sourceRole,
    };

    // ── the distribution ──────────────────────────────────────────────────

    [Fact]
    public async Task AMixedBatchRecordsTwoDifferentTrustLevels()
    {
        // THE falsifier. One request, two provenances. If this collapses to a single value the enum's
        // central distinction is lost at the exact moment it first carries weight.
        var extraction = WithFacts(
            Fact("Zurich", sourceRole: "user"),
            Fact("the 14:05 train", sourceRole: "assistant"));

        await CreateSut().PersistAsync(extraction, trustLevel: MemoryTrustLevel.UserProvided);

        _writtenFacts.Should().HaveCount(2);
        _writtenFacts.Single(f => f.Object == "Zurich").Metadata.GetTrustLevel()
            .Should().Be(MemoryTrustLevel.UserProvided);
        _writtenFacts.Single(f => f.Object == "the 14:05 train").Metadata.GetTrustLevel()
            .Should().Be(MemoryTrustLevel.ModelGenerated);
    }

    [Fact]
    public async Task AnAssistantSourcedPreferenceIsAlsoDistinguished()
    {
        // A preference the assistant attributed to the user becomes a durable statement about that
        // user, and afterwards is indistinguishable from one they actually stated.
        var extraction = new ExtractionStageResult
        {
            FilteredPreferences =
            [
                new ExtractedPreference { Category = "style", PreferenceText = "dark mode", Confidence = 0.9, SourceRole = "user" },
                new ExtractedPreference { Category = "travel", PreferenceText = "aisle seats", Confidence = 0.9, SourceRole = "assistant" },
            ],
        };

        await CreateSut().PersistAsync(extraction, trustLevel: MemoryTrustLevel.UserProvided);

        _writtenPreferences.Single(p => p.PreferenceText == "dark mode").Metadata.GetTrustLevel()
            .Should().Be(MemoryTrustLevel.UserProvided);
        _writtenPreferences.Single(p => p.PreferenceText == "aisle seats").Metadata.GetTrustLevel()
            .Should().Be(MemoryTrustLevel.ModelGenerated);
    }

    // ── the unchanged direction ───────────────────────────────────────────

    [Fact]
    public async Task NoReportedRoleLeavesTheRequestTrustLevelExactlyAsItWas()
    {
        // The byte-identical guarantee for every host on shipped defaults: with AssistantContentMode
        // .Ignore no extractor populates SourceRole, so every item takes this path and nothing moved.
        var extraction = WithFacts(Fact("Zurich", sourceRole: null));

        await CreateSut().PersistAsync(extraction, trustLevel: MemoryTrustLevel.VerifiedExternal);

        _writtenFacts.Should().ContainSingle()
            .Which.Metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.VerifiedExternal);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("system")]
    [InlineData("tool")]
    [InlineData("developer")]
    [InlineData("")]
    [InlineData("ASSISTANT_BOT")]
    public async Task NoOtherRoleMovesTrustInAnyDirection(string role)
    {
        // Only "assistant" is interpreted, on purpose. MemoryTrustLevel is ordered so >= means "at
        // least this trusted", and the default request trust is Untrusted -- so mapping "user" or
        // "tool" would RAISE trust on hosts that never asked for any, on the strength of a label the
        // model wrote about itself. Admission bypass and the system-role gate both compare with >=,
        // which makes that a security-relevant direction rather than a cosmetic one.
        var extraction = WithFacts(Fact("Zurich", sourceRole: role));

        await CreateSut().PersistAsync(extraction);

        _writtenFacts.Should().ContainSingle()
            .Which.Metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.Untrusted);
    }

    [Fact]
    public async Task TheRoleMatchIsCaseInsensitive()
    {
        var extraction = WithFacts(Fact("the 14:05 train", sourceRole: "Assistant"));

        await CreateSut().PersistAsync(extraction, trustLevel: MemoryTrustLevel.UserProvided);

        _writtenFacts.Should().ContainSingle()
            .Which.Metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.ModelGenerated);
    }

    [Fact]
    public async Task AHostsOwnDeclarationIsNeverDemotedByAModelSelfReport()
    {
        // ApplicationTrusted (5) outranks ModelGenerated (2). A host that declared the whole ingestion
        // trusted made a statement about the ingestion; refinement composes with that monotonic rule
        // rather than competing with it, so this is max, not override.
        var extraction = WithFacts(Fact("the 14:05 train", sourceRole: "assistant"));

        await CreateSut().PersistAsync(extraction, trustLevel: MemoryTrustLevel.ApplicationTrusted);

        _writtenFacts.Should().ContainSingle()
            .Which.Metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.ApplicationTrusted);
    }

    // ── the prompt side ───────────────────────────────────────────────────

    [Fact]
    public void TheDefaultPromptNeverAsksForARole()
    {
        // Prompt bytes are fingerprinted into every measured run. At Ignore nothing assistant-derived
        // is extracted, so the field would have one possible value and would buy nothing for the
        // sealed bases it invalidated.
        ExtractionPromptSemantics.AssistantContentInstruction(AssistantContentMode.Ignore)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(AssistantContentMode.Utterance)]
    [InlineData(AssistantContentMode.Fact)]
    public void BothAssistantModesAskForTheRole(AssistantContentMode mode)
    {
        // A mode that extracts assistant content without asking which turn it came from reintroduces
        // exactly the defect this closes, so neither mode may ship without the request.
        ExtractionPromptSemantics.AssistantContentInstruction(mode)
            .Should().Contain("source_role");
    }

    [Theory]
    [InlineData(AssistantContentMode.Utterance)]
    [InlineData(AssistantContentMode.Fact)]
    public void TheRoleRequestDefaultsToUserWhenTheModelIsUnsure(AssistantContentMode mode)
    {
        // The instruction must name a default, and it must be the conservative one. Left open, an
        // unsure model would pick freely -- and "assistant" is the value that RAISES trust here.
        ExtractionPromptSemantics.AssistantContentInstruction(mode)
            .Should().MatchRegex("(?i)unsure.*user");
    }
}
