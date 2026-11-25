using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.Extraction.Llm;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// ILC-07: the event-identity writer's operation contract. Reproduces the full Bariloche run's writer losses (turn 36
/// "op":"entity", turn 43 a quote the message does not contain, a checklist fact without its event observation) and the
/// missing channel for the writer's own stored-entity judgment ("the cutover" named after the stored go-live).
/// </summary>
public sealed class MemoryWriterContractTests : IDisposable
{
    private readonly IChatClient _chat = Substitute.For<IChatClient>();
    private readonly Queue<string> _replies = new();
    private readonly List<(List<ChatMessage> Messages, ChatOptions? Options)> _calls = [];
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly WarningLog _log = new();
    private readonly IIdentityCandidateProvider _candidates = Substitute.For<IIdentityCandidateProvider>();
    private readonly ServiceProvider _services;

    /// <summary>The writer's warnings, as text: a drop must reach a warning, not only Debug.</summary>
    private sealed class WarningLog : Microsoft.Extensions.Logging.ILogger<LlmMemoryWriter>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private const string GoLiveId = "identity-observation-golive";
    private static readonly Entity GoLive = new()
    {
        EntityId = GoLiveId, Name = "Acme Logistics billing system go-live", Type = "EVENT", OwnerId = "bruna",
        Confidence = 1, SourceMessageIds = ["prior"], CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    public MemoryWriterContractTests()
    {
        var facts = Substitute.For<IFactRepository>();
        facts.FindLatestObjectAsync(default!, default!, default!, default).ReturnsForAnyArgs((string?)null);
        facts.FindMentioningAsync(default!, default!, default, default).ReturnsForAnyArgs([]);
        facts.SearchByVectorAsync(default!, default(ValidTimeMode), default, default, default, default).ReturnsForAnyArgs([]);
        var preferences = Substitute.For<IPreferenceRepository>();
        preferences.FindMentioningAsync(default!, default!, default, default).ReturnsForAnyArgs([]);
        preferences.SearchByVectorAsync(default!, default, default, default, default).ReturnsForAnyArgs([]);
        _entities.FindLiveByNameAsync(default!, default, default!, default).ReturnsForAnyArgs((Entity?)null);
        _entities.SearchByVectorAsync(default!, default, default, default, default).ReturnsForAnyArgs([]);
        _entities.FindLiveBySeenNameAsync(default!, default!, default, default).ReturnsForAnyArgs([]);
        var relationships = Substitute.For<IRelationshipRepository>();
        relationships.GetLiveAroundAsync(default!, default!, default, default, default, default).ReturnsForAnyArgs([]);
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        _candidates.FindApproximateCandidatesAsync(default!, default, default).ReturnsForAnyArgs(new IdentityCandidateSet());
        _services = new ServiceCollection().AddSingleton(facts).AddSingleton(preferences).AddSingleton(_entities)
            .AddSingleton(relationships).AddSingleton(embeddings).AddSingleton(_messages).AddSingleton(_candidates).BuildServiceProvider();
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _calls.Add((call.Arg<IEnumerable<ChatMessage>>().ToList(), call.Arg<ChatOptions?>()));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, _replies.Count > 0 ? _replies.Dequeue() : "{\"ops\":[]}"));
        });
    }

    public void Dispose() => _services.Dispose();

    private void ShowStoredGoLive() =>
        _entities.SearchByVectorAsync(default!, default, default, default, default).ReturnsForAnyArgs([(GoLive, 0.9)]);

    private const string PersonId = "identity-observation-beatriz";

    private void ShowStored(string id, string name, string type) =>
        _entities.SearchByVectorAsync(default!, default, default, default, default).ReturnsForAnyArgs([(new Entity
        {
            EntityId = id, Name = name, Type = type, OwnerId = "bruna", Confidence = 1, SourceMessageIds = ["prior"],
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        }, 0.9)]);

    private void ShowStoredPerson() => ShowStored(PersonId, "Beatriz Molina", "PERSON");

    private Task<UnifiedExtractionResult> Write(string text, bool eventIdentities = true, params string[] before)
    {
        var writer = new LlmMemoryWriter(_chat, Options.Create(new LlmExtractionOptions
        { UseMemoryWriter = true, MemoryWriterEventIdentities = eventIdentities }), _services, _log);
        static Message Turn(string id, string content) => new() { MessageId = id, ConversationId = "c", SessionId = "s", Role = "user",
            Content = content, TimestampUtc = DateTimeOffset.UnixEpoch };
        return writer.WriteAsync(new(ExtractionWindow.ForTargets([Turn("current", text)]) with
        {
            Context = [.. before.Select((content, i) => Turn($"before-{i}", content))],
        }, MemoryScope.For("bruna", false)));
    }

    private const string DesignerId = "identity-observation-designer";

    /// <summary>The stored designer as whos-who-v1 statement 8 said her ("Valentina Ríos is the designer on Atlas."), and others shown beside her.</summary>
    private void ShowStoredDesigner(params Entity[] others)
    {
        var designer = new Entity
        {
            EntityId = DesignerId, Name = "Valentina Ríos", Type = "PERSON", OwnerId = "bruna", Confidence = 1,
            SourceMessageIds = ["m-designer"], CreatedAtUtc = DateTimeOffset.UnixEpoch,
        };
        _messages.GetByIdAsync("m-designer", Arg.Any<CancellationToken>()).Returns(new Message
        {
            MessageId = "m-designer", ConversationId = "c", SessionId = "s", Role = "user",
            Content = "Valentina Ríos is the designer on Atlas.", TimestampUtc = DateTimeOffset.UnixEpoch,
        });
        _entities.SearchByVectorAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs([(designer, 0.9), .. others.Select(e => (e, 0.8))]);
    }

    [Fact]
    public async Task Event_identity_writer_requests_a_schema_constrained_operation_reply()
    {
        await Write("Thanks.");
        var format = _calls.Should().ContainSingle().Which.Options!.ResponseFormat;
        format.Should().BeOfType<ChatResponseFormatJson>().Which.Schema.Should().NotBeNull();
    }

    [Fact]
    public async Task Default_writer_sends_no_response_format_and_asks_once()
    {
        await Write("Thanks.", eventIdentities: false);
        _calls.Should().ContainSingle().Which.Options!.ResponseFormat.Should().BeNull();
    }

    /// <summary>
    /// ILC-31: at the same room and reasoning effort GLM-5.3-Flash answered nothing to about 1 in 30 schema-constrained
    /// writer calls and never to the default writer's (six recorded runs each). The retry drops the schema and keeps the
    /// room (a constrained runaway is not cured by more); the contract still checks the reply.
    /// </summary>
    [Fact]
    public async Task An_empty_schema_reply_is_asked_again_without_the_schema_at_the_same_room()
    {
        _replies.Enqueue("");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"fact","text":"user | will write | the rollback checklist","quote":"I will write the rollback checklist"}]}""");

        var result = await Write("I will write the rollback checklist.");

        _calls.Should().HaveCount(2, "a clean retry needs no correction call");
        _calls[0].Options!.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>();
        _calls[1].Options!.ResponseFormat.Should().BeNull("the retry asks the way the default writer does");
        _calls[1].Options!.MaxOutputTokens.Should().Be(_calls[0].Options!.MaxOutputTokens);
        result.Facts.Should().ContainSingle();
    }

    [Fact]
    public async Task Three_empty_replies_still_fail_the_turn_loudly()
    {
        _replies.Enqueue(""); _replies.Enqueue(""); _replies.Enqueue("");

        var act = () => Write("I will write the rollback checklist.");

        await act.Should().ThrowAsync<FormatException>();
        _calls.Select(c => c.Options!.ResponseFormat is null).Should().Equal(false, true, true);
    }

    [Fact]
    public async Task A_clean_reply_is_not_corrected()
    {
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"fact","text":"user | will write | the rollback checklist","quote":"I will write the rollback checklist"}]}""");
        var result = await Write("I will write the rollback checklist.");
        _calls.Should().ContainSingle();
        result.Facts.Should().ContainSingle();
        result.Outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_action_gets_one_bounded_correction_instead_of_a_silent_missing_event()
    {
        _replies.Enqueue("""{"ops":[{"op":"entity","kind":"entity","text":"Lucia wedding (EVENT)","quote":"Lucia wedding"}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Lucia wedding (EVENT)","quote":"Lucia wedding"}]}""");
        var result = await Write("Lucia wedding is on December 12.");
        _calls.Should().HaveCount(2);
        _calls[1].Messages.Last().Text.Should().Contain("Lucia wedding (EVENT)");
        result.Entities.Should().ContainSingle().Which.Type.Should().Be("EVENT");
        result.Outcomes.Should().BeEmpty();
    }

    // ── IDENTITY-SHARED-NAME-FACTS: a bare first name shows every stored person it may mean ──
    // The eighth paid check's statement 42 ("Valentina moved to the Madrid office", 3 of 6 runs): the turn's bare name
    // found only the person stored under it (a niece), so the stored designer whose first name it is was never shown and
    // the writer could not refer to her; it wrote a new Valentina (a split). Our own example: two stored Noras.

    private static Entity Person(string id, string name) => new()
    {
        EntityId = id, Name = name, Type = "PERSON", OwnerId = "bruna", Confidence = 1, SourceMessageIds = ["prior"],
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task A_bare_first_name_shows_every_stored_person_it_may_mean_not_only_the_one_stored_under_it()
    {
        var niece = Person("identity-observation-nora", "Nora");
        var architect = Person("identity-observation-nora-lindqvist", "Nora Lindqvist");
        _entities.FindLiveByNameAsync("Nora", Arg.Any<string?>(), Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>()).Returns(niece);
        _candidates.FindApproximateCandidatesAsync(Arg.Is<IdentityCandidateQuery>(q => q.Name == "Nora" && q.Type == "PERSON"), Arg.Any<float[]?>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { Candidates = [new IdentityCandidate { Entity = niece }, new IdentityCandidate { Entity = architect }] });
        _replies.Enqueue("""{"ops":[]}""");

        await Write("Nora moved to the Oslo office.");

        var shown = _calls[0].Messages[1].Text;
        shown.Should().Contain("Nora Lindqvist (PERSON)", "the stored person whose first name it is can be referred to");
        shown.Should().Contain(": Nora (PERSON)", "the person stored under the bare name is still shown");
    }

    [Fact]
    public async Task A_full_name_in_the_turn_is_not_looked_up_word_by_word()
    {
        _replies.Enqueue("""{"ops":[]}""");
        await Write("Nora Lindqvist moved to the Oslo office.");
        await _candidates.DidNotReceive().FindApproximateCandidatesAsync(
            Arg.Is<IdentityCandidateQuery>(q => q.Name == "Nora" || q.Name == "Lindqvist"), Arg.Any<float[]?>(), Arg.Any<CancellationToken>());
    }

    // ── WRITER-SILENT-DROPS: the two drops of the full Bariloche run of 12 Nov, replayed with the model's recorded replies ──
    // (strategy/core/identity-resolution-validation/bariloche49-actual-complete-quality-failed, calls 37 and 44). Then they
    // were Debug lines and zero warnings. The worst case for the correction is the model repeating its mistake.

    private const string Turn36 = "The wedding is in Bariloche on Saturday, December 12. I already booked my flights: I will be in Bariloche from December 9 to December 13.";
    private const string Turn36Reply = """
        {"ops": [
        {"op": "entity", "kind": "entity", "text": "Lucía's wedding (EVENT)", "quote": "The wedding is in Bariloche on Saturday, December 12."},
        {"op": "add", "kind": "fact", "text": "Lucía's wedding | is scheduled for | Saturday, December 12", "quote": "The wedding is in Bariloche on Saturday, December 12."},
        {"op": "add", "kind": "fact", "text": "Lucía's wedding | takes place at | Bariloche", "quote": "The wedding is in Bariloche on Saturday, December 12."},
        {"op": "add", "kind": "fact", "text": "Bruna | will be in Bariloche from | December 9 to December 13", "quote": "I already booked my flights: I will be in Bariloche from December 9 to December 13."}
        ]}
        """;

    [Fact]
    public async Task Bariloche_turn_36_an_unknown_action_repeated_in_the_correction_surfaces_as_a_warning_and_a_rejected_outcome()
    {
        _replies.Enqueue(Turn36Reply);
        _replies.Enqueue(Turn36Reply);

        var result = await Write(Turn36);

        _calls.Should().HaveCount(2, "one correction, never a third call");
        _calls[1].Messages.Last().Text.Should().Contain("Lucía's wedding (EVENT)");
        result.Outcomes.Should().Contain(o => o.ErrorCode == MemoryErrorCodes.WriterOperationRejected && o.ErrorMessage!.Contains("Lucía's wedding"));
        _log.Warnings.Should().ContainSingle(w => w.Contains("remain after one correction") && w.Contains("Lucía's wedding"));
    }

    [Fact]
    public async Task Bariloche_turn_43_a_split_quote_repeated_in_the_correction_surfaces_and_the_valid_fact_is_kept()
    {
        const string reply = """{"ops": [{"op": "add", "kind": "fact", "text": "go-live runbook | needs | to be written by Bruna", "quote": "I still need to write the go-live runbook"}, {"op": "add", "kind": "fact", "text": "rollback checklist | needs | to be written by Bruna", "quote": "I still need to write... the rollback checklist"}]}""";
        _replies.Enqueue(reply);
        _replies.Enqueue(reply);

        var result = await Write("I still need to write the go-live runbook and the rollback checklist.");

        _calls.Should().HaveCount(2, "one correction, never a third call");
        result.Facts.Should().ContainSingle().Which.Subject.Should().Be("go-live runbook");
        result.Outcomes.Should().ContainSingle().Which.ErrorMessage.Should().Contain("rollback checklist");
        _log.Warnings.Should().ContainSingle(w => w.Contains("remain after one correction") && w.Contains("rollback checklist"));
    }

    [Fact]
    public async Task Invented_quote_is_corrected_without_accepting_the_unsupported_words()
    {
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"fact","text":"user | will write | rollback checklist","quote":"I have already written the checklist"}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"fact","text":"user | will write | rollback checklist","quote":"I will write the rollback checklist"}]}""");
        var result = await Write("I will write the rollback checklist.");
        _calls.Should().HaveCount(2);
        result.Facts.Should().ContainSingle().Which.SourceQuoteStart.Should().Be(0);
    }

    [Fact]
    public async Task A_correction_that_fails_again_is_reported_and_never_retried_a_third_time()
    {
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"fact","text":"user | will write | rollback checklist","quote":"I already wrote it"},{"op":"add","kind":"fact","text":"user | works at | Acme","quote":"I work at Acme"}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"fact","text":"user | will write | rollback checklist","quote":"still not in the message"}]}""");
        var result = await Write("I work at Acme and will write the rollback checklist.");
        _calls.Should().HaveCount(2);
        result.Facts.Should().ContainSingle().Which.Object.Should().Be("Acme");
        var rejected = result.Outcomes.Should().ContainSingle().Which;
        rejected.ErrorCode.Should().Be(MemoryErrorCodes.WriterOperationRejected);
        rejected.Status.Should().Be(IngestionItemStatus.Skipped);
        rejected.Kind.Should().Be(MemoryItemKind.Fact);
        rejected.ErrorMessage.Should().Contain("rollback checklist");
    }

    [Fact]
    public async Task An_event_fact_without_its_event_observation_asks_for_the_observation()
    {
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"fact","text":"Acme go-live | takes place at | Cordoba","quote":"The Acme go-live happens in Cordoba"},{"op":"add","kind":"entity","text":"Cordoba (PLACE)","quote":"Cordoba"}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Acme go-live (EVENT)","quote":"The Acme go-live"}]}""");
        var result = await Write("The Acme go-live happens in Cordoba.");
        _calls.Should().HaveCount(2);
        result.Entities.Should().Contain(e => e.Name == "Acme go-live" && e.Type == "EVENT");
        result.Facts.Should().ContainSingle().Which.Subject.Should().Be("Acme go-live");
    }

    [Fact]
    public async Task A_stored_entity_reference_maps_the_shown_label_to_its_id_and_located_quote()
    {
        ShowStoredGoLive();
        _replies.Enqueue("""
            {"ops":[
              {"op":"add","kind":"entity","text":"Acme Logistics billing system go-live (EVENT)","quote":"The cutover happens on-site","ref":"E1"},
              {"op":"add","kind":"fact","text":"Acme Logistics billing system go-live | takes place at | Acme's office in Cordoba","quote":"Acme's office in Cordoba"}
            ]}
            """);
        var result = await Write("The cutover happens on-site at Acme's office in Cordoba.");
        _calls.Should().ContainSingle();
        _calls[0].Messages[0].Text.Should().Contain("\"ref\"");
        var reference = result.Entities.Should().ContainSingle().Which.Reference;
        reference.Should().NotBeNull();
        reference!.EntityId.Should().Be(GoLiveId);
        reference.Quote.Should().Be("The cutover happens on-site");
    }

    [Fact]
    public async Task A_reference_to_an_item_that_was_not_shown_is_removed_and_reported_not_guessed()
    {
        ShowStoredGoLive();
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Acme Logistics billing system go-live (EVENT)","quote":"The cutover","ref":"E7"}]}""");
        _replies.Enqueue("""{"ops":[]}""");
        var result = await Write("The cutover happens on-site.");
        _calls.Should().HaveCount(2);
        result.Entities.Should().ContainSingle().Which.Reference.Should().BeNull();
        result.Outcomes.Should().ContainSingle().Which.ErrorCode.Should().Be(MemoryErrorCodes.WriterOperationRejected);
    }

    [Fact]
    public async Task A_reference_whose_name_differs_from_the_stored_name_is_not_kept()
    {
        ShowStoredGoLive();
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Acme cutover (EVENT)","quote":"The cutover","ref":"E1"}]}""");
        _replies.Enqueue("""{"ops":[]}""");
        var result = await Write("The cutover happens on-site.");
        _calls.Should().HaveCount(2);
        result.Entities.Should().ContainSingle().Which.Reference.Should().BeNull();
        result.Outcomes.Should().ContainSingle();
    }

    // ── IDENTITY-NICKNAME-SPLIT (whos-who-v1 tier 2: "Vale" for the stored "Valentina Ríos" stayed a person of its own) ──

    /// <summary>
    /// Six tier 2 runs: shown the stored person, the writer wrote the nickname as a new person with "ref" null; with a "ref",
    /// this contract demanded the stored name, so the nickname could only be lost. A short form of the stored person's name
    /// that the message uses is another name of that person: the mention takes the stored name (which tells namesakes apart),
    /// keeps the short form as an alias and keeps the reference, in one call.
    /// </summary>
    [Fact]
    public async Task A_person_called_by_a_short_form_of_their_stored_name_is_that_person_also_called_so()
    {
        ShowStoredPerson();
        _replies.Enqueue("""
            {"ops":[
              {"op":"add","kind":"entity","text":"Bea (PERSON)","quote":"Bea finished the floor plans today","ref":"E1"},
              {"op":"add","kind":"fact","text":"Beatriz Molina | finished | the floor plans","quote":"Bea finished the floor plans today"}
            ]}
            """);

        var result = await Write("Bea finished the floor plans today.");

        _calls.Should().ContainSingle("a short form of the stored name is no name mismatch to correct");
        var entity = result.Entities.Should().ContainSingle().Which;
        entity.Name.Should().Be("Beatriz Molina");
        entity.Type.Should().Be("PERSON");
        entity.Aliases.Should().Equal("Bea");
        entity.Reference!.EntityId.Should().Be(PersonId);
        result.Facts.Should().ContainSingle().Which.Subject.Should().Be("Beatriz Molina");
        result.Outcomes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Beatriz", "Beatriz finished the floor plans today")]   // a first name alone is part of the stored name
    [InlineData("Jonas", "Jonas finished the floor plans today")]       // another name altogether
    public async Task A_reference_under_any_other_name_still_needs_the_stored_name(string said, string message)
    {
        ShowStoredPerson();
        _replies.Enqueue($$"""{"ops":[{"op":"add","kind":"entity","text":"{{said}} (PERSON)","quote":"{{message}}","ref":"E1"}]}""");
        _replies.Enqueue("""{"ops":[]}""");

        var result = await Write(message + ".");

        _calls.Should().HaveCount(2, "the name mismatch is corrected as before");
        var entity = result.Entities.Should().ContainSingle().Which;
        entity.Name.Should().Be(said);
        entity.Reference.Should().BeNull();
        entity.Aliases.Should().BeEmpty();
    }

    [Fact]
    public async Task A_short_form_its_quote_does_not_hold_is_not_taken()
    {
        ShowStoredPerson();
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Bea (PERSON)","quote":"finished the floor plans today","ref":"E1"}]}""");
        _replies.Enqueue("""{"ops":[]}""");

        var result = await Write("Beatriz finished the floor plans today.");

        _calls.Should().HaveCount(2);
        result.Entities.Should().ContainSingle().Which.Reference.Should().BeNull();
    }

    [Theory]
    [InlineData("ORGANIZATION", "Northwind Analytics", "Northy (ORGANIZATION)", "Northy shipped the release")]
    [InlineData("EVENT", "Harvest festival", "Harvy (EVENT)", "Harvy starts at noon")]
    public async Task Only_a_person_is_referred_to_by_a_short_form(string type, string stored, string text, string message)
    {
        // An organisation, a place, a thing or an event keeps the exact stored-name rule (ILC-10).
        ShowStored("stored-thing", stored, type);
        _replies.Enqueue($$"""{"ops":[{"op":"add","kind":"entity","text":"{{text}}","quote":"{{message}}","ref":"E1"}]}""");
        _replies.Enqueue("""{"ops":[]}""");

        var result = await Write(message + ".");

        _calls.Should().HaveCount(2);
        result.Entities.Should().ContainSingle().Which.Reference.Should().BeNull();
    }

    [Fact]
    public async Task A_reference_on_a_fact_is_ignored_without_a_correction_call_or_a_rejection()
    {
        // Full Bariloche rerun: the schema asks for "ref" on every operation and the model filled it on facts; the
        // contract spent ten correction calls and reported kept facts as rejected.
        ShowStoredGoLive();
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"fact","text":"user | leads | the go-live","quote":"I lead the go-live","ref":"E1"}]}""");
        var result = await Write("I lead the go-live.");
        _calls.Should().ContainSingle();
        result.Facts.Should().ContainSingle();
        result.Outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_malformed_entity_text_is_corrected_instead_of_coerced_into_an_object()
    {
        // Full Bariloche rerun: "Nexo (ORGANIZATION) | is a small software studio" was stored as an OBJECT named after the whole text.
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Nexo (ORGANIZATION) | is a small software studio","quote":"Nexo, a small software studio"}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Nexo (ORGANIZATION)","quote":"Nexo, a small software studio"}]}""");
        var result = await Write("I'm Bruna, tech lead at Nexo, a small software studio.");
        _calls.Should().HaveCount(2);
        result.Entities.Should().ContainSingle().Which.Should().Match<ExtractedEntity>(e => e.Name == "Nexo" && e.Type == "ORGANIZATION");
        result.Outcomes.Should().BeEmpty();
    }

    /// <summary>
    /// IDENTITY-WRITER-COVERAGE guard: a thing named inside a fact or a preference, written beside it as its own entity,
    /// passes the contract in one call and keeps its kind; nothing between the writer and identity drops these kinds.
    /// </summary>
    [Fact]
    public async Task Named_things_beside_a_fact_and_a_preference_pass_the_contract_with_their_kinds()
    {
        _replies.Enqueue("""
            {"ops":[
              {"op":"add","kind":"fact","text":"user | is reading | Field Notes on Rivers","quote":"I'm reading Field Notes on Rivers"},
              {"op":"add","kind":"entity","text":"Field Notes on Rivers (THING)","quote":"Field Notes on Rivers","ref":null},
              {"op":"add","kind":"preference","text":"Only buys boots from Northwind Outfitters","quote":"I only buy boots from Northwind Outfitters"},
              {"op":"add","kind":"entity","text":"Northwind Outfitters (ORGANIZATION)","quote":"Northwind Outfitters","ref":null},
              {"op":"add","kind":"entity","text":"finance team (ORGANIZATION)","quote":"the finance team","ref":null}
            ]}
            """);
        var result = await Write("I'm reading Field Notes on Rivers, I only buy boots from Northwind Outfitters, and the finance team wants the summary.");
        _calls.Should().ContainSingle();
        result.Entities.Should().Contain(e => e.Name == "Field Notes on Rivers" && e.Type == "OBJECT");
        result.Entities.Should().Contain(e => e.Name == "Northwind Outfitters" && e.Type == "ORGANIZATION");
        result.Entities.Should().Contain(e => e.Name == "finance team" && e.Type == "ORGANIZATION");
        result.Facts.Should().ContainSingle();
        result.Preferences.Should().ContainSingle();
        result.Outcomes.Should().BeEmpty();
    }

    /// <summary>
    /// IDENTITY-WRITER-COVERAGE, the paid check of 23 Nov (ikb-cover-1): the writer stored a second café under exactly the
    /// stored one's name, so the next turn was shown two stored "Café ... (LOCATION)" it could not tell apart, and added a
    /// third. A new entity named and typed exactly as a shown stored one (not an event) is asked once which it is.
    /// </summary>
    [Fact]
    public async Task A_stored_places_name_said_again_is_asked_once_and_left_out_when_it_is_the_same_place()
    {
        // The second paid check (ikb-cover2-1, statement 41): a writer's ref cannot confirm a place or a thing (only a
        // person, an organisation or an event), so a re-mention given its ref stayed pending: another identity of it.
        ShowStored("place-lumen", "Café Lumen", "LOCATION");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Café Lumen (PLACE)","quote":"Café Lumen","ref":null},{"op":"add","kind":"fact","text":"Café Lumen | has | live jazz on Fridays","quote":"Café Lumen has live jazz on Fridays","ref":null}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Café Lumen (PLACE)","quote":"Café Lumen","ref":"E1"}]}""");

        var result = await Write("Café Lumen has live jazz on Fridays.");

        _calls.Should().HaveCount(2);
        _calls[1].Messages.Last().Text.Should().Contain("E1").And.Contain("leave it out").And.Contain("tell it apart");
        result.Entities.Should().BeEmpty("the same stored place needs no new observation: its fact carries it");
        result.Facts.Should().ContainSingle().Which.Subject.Should().Be("Café Lumen");
        result.Outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stored_organisations_name_said_again_is_asked_once_and_its_reference_kept()
    {
        ShowStored("org-nexo", "Nexo Studio", "ORGANIZATION");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Nexo Studio (ORGANIZATION)","quote":"Nexo Studio","ref":null}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Nexo Studio (ORGANIZATION)","quote":"Nexo Studio","ref":"E1"}]}""");

        var result = await Write("Nexo Studio hired two designers.");

        _calls.Should().HaveCount(2);
        _calls[1].Messages.Last().Text.Should().Contain("give its \"ref\"");
        result.Entities.Should().ContainSingle().Which.Reference!.EntityId.Should().Be("org-nexo");
        result.Outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_different_thing_of_a_stored_name_is_kept_under_the_name_that_tells_it_apart()
    {
        ShowStored("place-lumen", "Café Lumen", "LOCATION");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Café Lumen (PLACE)","quote":"Café Lumen","ref":null}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Café Lumen in Porto (PLACE)","quote":"Café Lumen in Porto","ref":null}]}""");

        var result = await Write("There is another Café Lumen in Porto, open only at night.");

        _calls.Should().HaveCount(2);
        var entity = result.Entities.Should().ContainSingle().Which;
        entity.Name.Should().Be("Café Lumen in Porto");
        entity.Reference.Should().BeNull();
        result.Outcomes.Should().BeEmpty();
    }

    /// <summary>
    /// IDENTITY-HARD-CASES A3 (whos-who-v2 run 2): asked which it was, the writer told the neighbours' puppy from the stored
    /// dog of the same name as "Name (who it belongs to) (PERSON)". The words in brackets are no valid entity name, so the
    /// fix was not taken, the entity stayed under the bare stored name, and identity merged the two dogs. A correction that
    /// tells a repeated name apart in brackets is that name with its qualifier: kept, without a reference.
    /// </summary>
    [Fact]
    public async Task A_repeated_name_told_apart_in_brackets_is_kept_under_the_name_and_its_qualifier()
    {
        ShowStored("pet-luna", "Luna", "PERSON");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Luna (PERSON)","quote":"named her Luna too","ref":null}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Luna (the neighbours' kitten) (PERSON)","quote":"named her Luna too","ref":null}]}""");

        var result = await Write("The neighbours got a kitten and named her Luna too.");

        _calls.Should().HaveCount(2);
        var entity = result.Entities.Should().ContainSingle().Which;
        entity.Name.Should().Be("Luna, the neighbours' kitten");
        entity.Type.Should().Be("PERSON");
        entity.Reference.Should().BeNull();
        result.Outcomes.Should().BeEmpty();
    }

    /// <summary>
    /// IDENTITY-HARD-CASES E (whos-who-v2, every run): "it's Ms. Okafor, not Okoro". The writer referred the corrected name
    /// to the stored Ms. Okoro, rightly, and the contract refused a reference under another name, so the correction became
    /// a second person. A name the message gives in place of a stored one, saying both with a correction or a rename, is
    /// that stored entity, also called by the new name.
    /// </summary>
    [Theory]
    [InlineData("Ms. Ortega", "PERSON", "Ms. Vidal", "I've been getting her name wrong: it's Ms. Vidal, not Ortega.", "it's Ms. Vidal, not Ortega")]
    [InlineData("Nexo Studio", "ORGANIZATION", "Brightline", "Nexo Studio has a new name: it's Brightline now.", "Nexo Studio has a new name: it's Brightline now")]
    [InlineData("Nexo Studio", "ORGANIZATION", "Brightline", "Nexo Studio was renamed Brightline last week.", "Nexo Studio was renamed Brightline")]
    public async Task A_stored_name_corrected_or_renamed_in_the_message_is_that_entity_also_called_by_the_new_name(
        string stored, string type, string renamed, string message, string quote)
    {
        ShowStored("stored-1", stored, type);
        _replies.Enqueue($$"""{"ops":[{"op":"add","kind":"entity","text":"{{renamed}} ({{type}})","quote":"{{quote}}","ref":"E1"}]}""");

        var result = await Write(message);

        _calls.Should().ContainSingle("a correction or a rename is no name mismatch to correct");
        var entity = result.Entities.Should().ContainSingle().Which;
        entity.Name.Should().Be(stored);
        entity.Aliases.Should().Equal(renamed);
        entity.Reference!.EntityId.Should().Be("stored-1");
        result.Outcomes.Should().BeEmpty();
    }

    /// <summary>
    /// The paid check of 24 Nov (ihc1-v1-glm-2 and 3, statement 50, "Brisa is called Brisa Labs now"): told that a rename
    /// is about the stored entity, the writer answered with an entity "replace" and a text holding both names; the text was
    /// no entity name, so a junk thing was stored and the studio split. An entity is always an add; its leading "Name
    /// (TYPE)" is the entity, and "is called ... now" says a rename.
    /// </summary>
    [Theory]
    [InlineData("Nexo Labs (ORGANIZATION) (replacing: Nexo Studio (ORGANIZATION))")]
    [InlineData("Nexo Labs (ORGANIZATION) replaces Nexo Studio (ORGANIZATION) (renamed) - stored as E1")]
    public async Task A_rename_answered_as_an_entity_replace_is_the_stored_entity_also_called_by_the_new_name(string text)
    {
        ShowStored("stored-1", "Nexo Studio", "ORGANIZATION");
        _replies.Enqueue($$"""{"ops":[{"op":"replace","kind":"entity","id":"E1","text":"{{text}}","quote":"Nexo Studio is called Nexo Labs now.","ref":"E1"}]}""");

        var result = await Write("Nexo Studio is called Nexo Labs now.");

        _calls.Should().ContainSingle();
        var entity = result.Entities.Should().ContainSingle().Which;
        entity.Name.Should().Be("Nexo Studio");
        entity.Aliases.Should().Equal("Nexo Labs");
        entity.Reference!.EntityId.Should().Be("stored-1");
    }

    [Theory]
    [InlineData("Ms. Vidal called about the will.", "Ms. Vidal called")]                       // the stored name is not said
    [InlineData("Ms. Vidal and Ms. Ortega both called.", "Ms. Vidal and Ms. Ortega both called")] // both said, no correction
    public async Task Another_name_without_a_correction_or_a_rename_still_needs_the_stored_name(string message, string quote)
    {
        ShowStored("stored-1", "Ms. Ortega", "PERSON");
        _replies.Enqueue($$"""{"ops":[{"op":"add","kind":"entity","text":"Ms. Vidal (PERSON)","quote":"{{quote}}","ref":"E1"}]}""");
        _replies.Enqueue("""{"ops":[]}""");

        await Write(message);

        _calls.Should().HaveCount(2, "the name mismatch is corrected as before");
    }

    [Fact]
    public async Task A_first_answer_with_a_description_in_brackets_is_still_asked_to_fix_its_form()
    {
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Ms. Ortega (a lawyer) (PERSON)","quote":"a lawyer called Ms. Ortega","ref":null}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Ms. Ortega (PERSON)","quote":"a lawyer called Ms. Ortega","ref":null}]}""");

        var result = await Write("I'm making a will with a lawyer called Ms. Ortega.");

        _calls.Should().HaveCount(2, "a bracketed description is no name: only a repeated name's qualifier is kept");
        result.Entities.Should().ContainSingle().Which.Name.Should().Be("Ms. Ortega");
    }

    [Fact]
    public async Task A_stored_events_name_observed_again_is_not_asked()
    {
        ShowStoredGoLive();
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Acme Logistics billing system go-live (EVENT)","quote":"Acme Logistics billing system go-live","ref":null},{"op":"add","kind":"fact","text":"Acme Logistics billing system go-live | is scheduled for | Friday","quote":"Friday"}]}""");

        await Write("The Acme Logistics billing system go-live is on Friday.");

        _calls.Should().ContainSingle();
    }

    [Fact]
    public async Task A_malformed_entity_that_stays_malformed_is_dropped_and_reported_never_stored_as_an_object()
    {
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"training session (EVENT)},{","quote":"training session"}]}""");
        _replies.Enqueue("""{"ops":[]}""");
        var result = await Write("Acme asked for a training session.");
        _calls.Should().HaveCount(2);
        result.Entities.Should().BeEmpty();
        result.Outcomes.Should().ContainSingle().Which.Kind.Should().Be(MemoryItemKind.Entity);
    }

    [Fact]
    public async Task A_correction_that_fixes_only_the_quote_keeps_its_malformed_entity_out_of_the_store()
    {
        // ILC-23 ECB+ igb-ecbplus-new-02: "Arcade Joseph Comeaux Jr (PERSON) | null" with an inexact quote. The correction
        // fixed the quote only; the merge accepted it on its text and stored an OBJECT named after the whole text.
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Arcade Comeaux (PERSON) | null","quote":"Arcade Comeaux, Prison Inmate"}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Arcade Comeaux (PERSON) | null","quote":"Comeaux, 49"}]}""");
        var result = await Write("Arcade Comeaux, 49, fled on foot.");
        _calls.Should().HaveCount(2);
        result.Entities.Should().BeEmpty();
        var outcome = result.Outcomes.Should().ContainSingle().Which;
        outcome.Kind.Should().Be(MemoryItemKind.Entity);
        outcome.ErrorMessage.Should().Contain("Name (PERSON|PLACE|ORGANIZATION|THING|EVENT)");
    }

    [Fact]
    public async Task A_connection_with_an_empty_end_is_reported_when_dropped()
    {
        // ILC-34 review 3: an end with no word names nothing. Only a new name could fix it, so it is not sent back for a
        // correction: dropped and reported at once.
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"relationship","text":"Ana -[LIKES]-> ...","quote":"Ana likes"}]}""");
        var result = await Write("Ana likes...");
        _calls.Should().HaveCount(1);
        result.Relationships.Should().BeEmpty();
        result.Facts.Should().BeEmpty();
        result.Outcomes.Should().ContainSingle().Which.ErrorMessage.Should().Contain(nameof(MemoryWriterOps.DropReason.ConnectionEndEmpty));
    }

    [Fact]
    public async Task An_empty_end_never_takes_the_correction_meant_for_another_operation()
    {
        // ILC-34 review 5: the empty-end connection (written as an entity) is not sent for a correction, so the corrected
        // "Ana (PERSON)" is the fix of the entity whose quote was wrong, and only the empty end is reported.
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Ana -[LIKES]-> ...","quote":"Ana likes"},{"op":"add","kind":"entity","text":"Ana (PERSON)","quote":"Ana Lopez"}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Ana (PERSON)","quote":"Ana likes"}]}""");
        var result = await Write("Ana likes jazz.");
        _calls.Should().HaveCount(2);
        result.Entities.Should().ContainSingle().Which.Name.Should().Be("Ana");
        result.Outcomes.Should().ContainSingle().Which.ErrorMessage.Should().Contain(nameof(MemoryWriterOps.DropReason.ConnectionEndEmpty));
    }

    [Fact]
    public async Task A_correction_that_fixes_only_the_quote_keeps_its_json_debris_out_of_the_store()
    {
        // The same gap for facts: a quote-only fix was accepted on its unchanged text, debris included.
        _replies.Enqueue("{\"ops\":[{\"op\":\"add\",\"kind\":\"fact\",\"text\":\"user | likes | mate```\",\"quote\":\"I love mate\"}]}");
        _replies.Enqueue("{\"ops\":[{\"op\":\"add\",\"kind\":\"fact\",\"text\":\"user | likes | mate```\",\"quote\":\"I drink mate\"}]}");
        var result = await Write("I drink mate all day.");
        _calls.Should().HaveCount(2);
        result.Facts.Should().BeEmpty();
        result.Outcomes.Should().ContainSingle().Which.ErrorMessage.Should().Contain(nameof(MemoryWriterOps.DropReason.TextFormatInvalid));
    }

    [Fact]
    public async Task A_dropped_entity_with_a_malformed_text_is_asked_to_fix_both_in_one_correction()
    {
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Arcade Comeaux (PERSON) | null","quote":"Arcade Comeaux, Prison Inmate"}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Arcade Comeaux (PERSON)","quote":"Arcade Comeaux"}]}""");
        var result = await Write("Arcade Comeaux, 49, fled on foot.");
        _calls.Should().HaveCount(2);
        _calls[1].Messages[^1].Text.Should().Contain("not exact").And.Contain("Name (PERSON|PLACE|ORGANIZATION|THING|EVENT)");
        result.Entities.Should().ContainSingle().Which.Should().Match<ExtractedEntity>(e => e.Name == "Arcade Comeaux" && e.Type == "PERSON");
        result.Outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_fact_carrying_json_debris_is_corrected_instead_of_stored_with_a_lost_date()
    {
        // Full Bariloche real-04: "December 4\n```json\n{\"ops\": ...}```" was stored as the object; the repeated date then
        // (correctly) kept no date field, and the store check for the code freeze failed.
        _replies.Enqueue("{\"ops\":[{\"op\":\"add\",\"kind\":\"fact\",\"text\":\"the billing system | has a code freeze starting | December 4\\n```json\\n{\\\"ops\\\": []}\\n```\",\"quote\":\"code freeze for the billing system starting December 4\"}]}");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"fact","text":"the billing system | has a code freeze starting | December 4","quote":"code freeze for the billing system starting December 4"}]}""");
        var result = await Write("We agreed on a code freeze for the billing system starting December 4.");
        _calls.Should().HaveCount(2);
        result.Facts.Should().ContainSingle().Which.Object.Should().Be("December 4");
        result.Outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_fact_whose_debris_survives_the_correction_is_dropped_and_reported()
    {
        _replies.Enqueue("{\"ops\":[{\"op\":\"add\",\"kind\":\"fact\",\"text\":\"user | likes | mate```\",\"quote\":\"I drink mate\"}]}");
        _replies.Enqueue("""{"ops":[]}""");
        var result = await Write("I drink mate all day.");
        result.Facts.Should().BeEmpty();
        result.Outcomes.Should().ContainSingle().Which.Kind.Should().Be(MemoryItemKind.Fact);
    }

    [Fact]
    public async Task A_reference_problem_is_reported_as_a_skipped_reference_not_a_skipped_entity()
    {
        ShowStoredGoLive();
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Acme Logistics billing system go-live (EVENT)","quote":"The cutover","ref":"E7"}]}""");
        _replies.Enqueue("""{"ops":[]}""");
        var result = await Write("The cutover happens on-site.");
        result.Entities.Should().ContainSingle();
        var outcome = result.Outcomes.Should().ContainSingle().Which;
        outcome.SourceKey.Should().EndWith(":ref");
        outcome.ErrorMessage.Should().Contain("kept without its reference");
    }

    [Fact]
    public async Task A_schema_reply_without_json_is_retried_at_the_same_room()
    {
        // Full Bariloche rerun turn 26: a runaway repetition filled the room; a doubled room cannot help a constrained reply
        // and was refused by the evaluation's output guard, sending the turn to the extractors.
        _replies.Enqueue("{\"ops\": [{\"op\": \"add\", \"text\": \"meetup (EVENT) (EVENT) (EVENT)");
        _replies.Enqueue("""{"ops":[]}""");
        await Write("I'd like to give a talk at a meetup.");
        _calls.Should().HaveCount(2);
        _calls[1].Options!.MaxOutputTokens.Should().Be(_calls[0].Options!.MaxOutputTokens);
    }

    [Fact]
    public async Task Default_writer_ignores_a_spontaneous_reference()
    {
        ShowStoredGoLive();
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Acme Logistics billing system go-live (EVENT)","quote":"The cutover","ref":"E1"}]}""");
        var result = await Write("The cutover happens on-site.", eventIdentities: false);
        _calls.Should().ContainSingle();
        result.Entities.Should().ContainSingle().Which.Reference.Should().BeNull();
        result.Outcomes.Should().BeEmpty();
    }

    // ── IDENTITY-PERSON-REF-GUARD (the third paid check, cover3-2 statement 13: "My niece Valentina started school this year,
    // she is six." was referred to the stored designer Valentina Ríos and her facts written under the designer's name.
    // Identity's relation guard refused the merge, but the facts had already been stored under the wrong person.) ──

    /// <summary>
    /// The message calls the person "my niece Valentina"; the stored Valentina Ríos is known as the designer. Identity's
    /// own relation words say these are two people, so the writer's reference is refused before anything is stored: the
    /// entity keeps the message's own name, and every fact, preference and connection of the reply that named the stored
    /// person names the message's person instead. No correction call: nothing is left for the model to decide.
    /// </summary>
    [Fact]
    public async Task A_person_reference_the_messages_relation_contradicts_is_refused_and_its_facts_keep_the_messages_name()
    {
        ShowStoredDesigner();
        _replies.Enqueue("""
            {"ops":[
              {"op":"add","kind":"entity","text":"Valentina Ríos (PERSON)","quote":"My niece Valentina","ref":"E1"},
              {"op":"add","kind":"fact","text":"Valentina Ríos | started school | this year","quote":"started school this year","ref":null},
              {"op":"add","kind":"fact","text":"Valentina Ríos | is | six years old","quote":"she is six","ref":null},
              {"op":"add","kind":"preference","text":"Valentina Ríos loves her new school","quote":"started school this year","ref":null}
            ]}
            """);

        var result = await Write("My niece Valentina started school this year, she is six.");

        _calls.Should().ContainSingle("the refusal is deterministic: no correction call");
        var entity = result.Entities.Should().ContainSingle().Which;
        entity.Name.Should().Be("Valentina");
        entity.Type.Should().Be("PERSON");
        entity.Reference.Should().BeNull();
        entity.Aliases.Should().BeEmpty();
        result.Facts.Select(f => f.Subject).Should().Equal("Valentina", "Valentina");
        result.Preferences.Should().ContainSingle().Which.PreferenceText.Should().Be("Valentina loves her new school");
        result.Outcomes.Should().ContainSingle().Which.ErrorMessage.Should()
            .Contain(nameof(MemoryWriterOps.DropReason.ReferenceRelationConflict)).And.Contain("niece").And.Contain("designer");
    }

    /// <summary>
    /// cover3, statement 14: "Valentina lost her first tooth yesterday." names no relation itself; the turn before said
    /// "My niece Valentina". The latest earlier turn of the window that names her is read for the relation.
    /// </summary>
    [Fact]
    public async Task A_person_reference_the_turn_before_contradicts_is_refused_too()
    {
        ShowStoredDesigner();
        _replies.Enqueue("""
            {"ops":[
              {"op":"add","kind":"entity","text":"Valentina Ríos (PERSON)","quote":"Valentina lost her first tooth","ref":"E1"},
              {"op":"add","kind":"fact","text":"Valentina Ríos | lost | her first tooth","quote":"Valentina lost her first tooth yesterday","ref":null}
            ]}
            """);

        var result = await Write("Valentina lost her first tooth yesterday.", true,
            "My dog Luna is a border collie.", "My niece Valentina started school this year, she is six.");

        _calls.Should().ContainSingle();
        result.Entities.Should().ContainSingle().Which.Should().Match<ExtractedEntity>(e => e.Name == "Valentina" && e.Reference == null);
        result.Facts.Should().ContainSingle().Which.Subject.Should().Be("Valentina");
    }

    /// <summary>Only the latest earlier turn that names her counts: a later one that says nothing of a niece settles it.</summary>
    [Fact]
    public async Task Only_the_latest_earlier_turn_that_names_the_person_is_read_for_a_relation()
    {
        ShowStoredDesigner();
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Valentina Ríos (PERSON)","quote":"Valentina moved","ref":"E1"}]}""");

        var result = await Write("Valentina moved to the Madrid office.", true,
            "My niece Valentina started school this year.", "Valentina Ríos sent the Atlas mock-ups.");

        result.Entities.Should().ContainSingle().Which.Reference!.EntityId.Should().Be(DesignerId);
        result.Outcomes.Should().BeEmpty();
    }

    /// <summary>A relation is read for the name the message calls the person by, not for every word of the stored name.</summary>
    [Fact]
    public async Task A_relation_given_to_someone_else_of_a_like_name_does_not_refuse_the_reference()
    {
        ShowStoredDesigner();
        _replies.Enqueue("""
            {"ops":[
              {"op":"add","kind":"entity","text":"Valentina (PERSON)","quote":"My niece Valentina","ref":null},
              {"op":"add","kind":"entity","text":"Vale (PERSON)","quote":"draw like Vale","ref":"E1"}
            ]}
            """);

        var result = await Write("My niece Valentina wants to draw like Vale.");

        _calls.Should().ContainSingle();
        var vale = result.Entities.Should().ContainSingle(e => e.Reference != null).Which;
        vale.Name.Should().Be("Valentina Ríos");
        vale.Aliases.Should().Equal("Vale");
        result.Outcomes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Valentina sent over the new mock-ups.", "Valentina sent over the new mock-ups")]          // no relation said
    [InlineData("Valentina, our designer, sent over the new mock-ups.", "Valentina, our designer")]       // the relation she is known by
    public async Task A_person_reference_no_relation_contradicts_is_kept(string message, string quote)
    {
        ShowStoredDesigner();
        _replies.Enqueue($$"""{"ops":[{"op":"add","kind":"entity","text":"Valentina Ríos (PERSON)","quote":"{{quote}}","ref":"E1"}]}""");

        var result = await Write(message);

        _calls.Should().ContainSingle();
        var entity = result.Entities.Should().ContainSingle().Which;
        entity.Name.Should().Be("Valentina Ríos");
        entity.Reference!.EntityId.Should().Be(DesignerId);
        result.Outcomes.Should().BeEmpty();
    }

    /// <summary>
    /// A message that holds the stored full name names the stored person itself, beside whoever else it names: the guard is
    /// for a reference that put the stored name where the message used a shorter one. Identity's own guard still reads it.
    /// </summary>
    [Fact]
    public async Task A_message_that_holds_the_stored_full_name_keeps_its_reference_for_identity_to_judge()
    {
        ShowStoredDesigner();
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Valentina Ríos (PERSON)","quote":"Valentina Ríos","ref":"E1"}]}""");

        var result = await Write("My niece Valentina met Valentina Ríos at the studio.");

        _calls.Should().ContainSingle();
        result.Entities.Should().ContainSingle().Which.Reference!.EntityId.Should().Be(DesignerId);
        result.Outcomes.Should().BeEmpty();
    }

    /// <summary>
    /// The third check, all four runs: shown "Valentina Ríos", the writer wrote "Vale" as a new person with "ref" null. A new
    /// person named by a short form of a shown stored person's name is asked once which it is.
    /// </summary>
    [Fact]
    public async Task A_new_person_named_by_a_short_form_of_a_shown_stored_person_is_asked_once()
    {
        ShowStoredDesigner();
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Vale (PERSON)","quote":"Vale finished the new checkout mock-ups","ref":null}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Vale (PERSON)","quote":"Vale finished the new checkout mock-ups","ref":"E1"}]}""");

        var result = await Write("Vale finished the new checkout mock-ups today.");

        _calls.Should().HaveCount(2);
        _calls[1].Messages[^1].Text.Should().Contain("E1").And.Contain("Valentina Ríos").And.Contain("short form");
        var entity = result.Entities.Should().ContainSingle().Which;
        entity.Name.Should().Be("Valentina Ríos");
        entity.Aliases.Should().Equal("Vale");
        entity.Reference!.EntityId.Should().Be(DesignerId);
        result.Outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_short_form_answered_as_someone_else_is_kept_as_written_without_a_rejection()
    {
        ShowStoredDesigner();
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Vale (PERSON)","quote":"Vale finished the new checkout mock-ups","ref":null}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Vale (PERSON)","quote":"Vale finished the new checkout mock-ups","ref":null}]}""");

        var result = await Write("Vale finished the new checkout mock-ups today.");

        _calls.Should().HaveCount(2);
        result.Entities.Should().ContainSingle().Which.Should().Match<ExtractedEntity>(e => e.Name == "Vale" && e.Reference == null);
        result.Outcomes.Should().BeEmpty("the writer said who it is: someone else");
    }

    [Theory]
    [InlineData("My niece Vale lost her first tooth.", "Vale", "My niece Vale")]       // the relation contradicts the stored designer
    [InlineData("Valentina lost her first tooth.", "Valentina", "Valentina lost")]     // a first name alone is part of the name, not a short form
    public async Task A_person_who_cannot_be_the_stored_one_by_a_short_form_is_not_asked(string message, string name, string quote)
    {
        ShowStoredDesigner();
        _replies.Enqueue($$"""{"ops":[{"op":"add","kind":"entity","text":"{{name}} (PERSON)","quote":"{{quote}}","ref":null}]}""");

        await Write(message);

        _calls.Should().ContainSingle();
    }

    /// <summary>A name the stored person is already also called is asked too, though it is no short form of the stored name ("Pepe" for José).</summary>
    [Fact]
    public async Task A_new_person_named_as_a_stored_persons_other_name_is_asked_once_and_referred()
    {
        _entities.SearchByVectorAsync(default!, default, default, default, default).ReturnsForAnyArgs([(new Entity
        {
            EntityId = "identity-observation-jose", Name = "José García", Type = "PERSON", OwnerId = "bruna", Confidence = 1,
            Aliases = ["Pepe"], SourceMessageIds = ["prior"], CreatedAtUtc = DateTimeOffset.UnixEpoch,
        }, 0.9)]);
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Pepe (PERSON)","quote":"Pepe called","ref":null}]}""");
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Pepe (PERSON)","quote":"Pepe called","ref":"E1"}]}""");

        var result = await Write("Pepe called about the boat.");

        _calls.Should().HaveCount(2);
        var entity = result.Entities.Should().ContainSingle().Which;
        entity.Name.Should().Be("José García");
        entity.Aliases.Should().Equal("Pepe");
        entity.Reference!.EntityId.Should().Be("identity-observation-jose");
    }

    /// <summary>
    /// IDENTITY-CONFIRMED-ALIAS, the fourth paid check, statement 53: "Vale and I presented Atlas to the client." The designer
    /// was confirmed as "Vale" at statement 11, but a name lookup reads names and aliases only, so she was not shown and the
    /// writer wrote a new person. A name the store does not know is looked up among the names identity has seen people
    /// under; the person found is shown with it ("also called Vale"), so the writer can refer to her.
    /// </summary>
    [Fact]
    public async Task A_person_seen_under_a_short_form_is_shown_when_a_turn_says_it_alone()
    {
        var designer = new Entity
        {
            EntityId = DesignerId, Name = "Valentina Ríos", Type = "PERSON", OwnerId = "bruna", Confidence = 1,
            SourceMessageIds = ["m-designer"], CreatedAtUtc = DateTimeOffset.UnixEpoch,
        };
        _entities.FindLiveBySeenNameAsync("Vale", Arg.Any<MemoryScope>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([designer]);
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"entity","text":"Vale (PERSON)","quote":"Vale and I","ref":"E1"}]}""");

        var result = await Write("Vale and I presented Atlas to the client.");

        _calls.Should().ContainSingle();
        _calls[0].Messages[1].Text.Should().Contain("E1: Valentina Ríos (PERSON), also called Vale");
        var entity = result.Entities.Should().ContainSingle().Which;
        entity.Name.Should().Be("Valentina Ríos");
        entity.Aliases.Should().Equal("Vale");
        entity.Reference!.EntityId.Should().Be(DesignerId);
    }

    [Fact]
    public async Task The_default_writer_does_not_look_names_up_among_the_names_identity_has_seen()
    {
        await Write("Vale and I presented Atlas to the client.", eventIdentities: false);

        await _entities.DidNotReceiveWithAnyArgs().FindLiveBySeenNameAsync(default!, default!, default, default);
    }

    // ── IDENTITY-FACT-NAME-GUARD (the fifth paid check, cover5-2 and cover5-4 statement 13: the writer answered only facts,
    // "Valentina Ríos | started school | 2026" and "Valentina Ríos | is | six years old", with no entity operation, so there
    // was no reference for the guard above to refuse and the niece's facts went under the designer's name.) ──

    /// <summary>
    /// IDENTITY-HARD-CASES A4, the paid check of 24 Nov (ihc1-v2-glm-1, statement 18): a person stored by a first name only,
    /// met as someone's parent; "My boss X, ..." answered with facts about "X" and no entity. The message holds the stored
    /// name itself, so the guard above left it, the boss's facts went under the parent's name, and a later "the boss" was
    /// confirmed as the parent. Such facts take the name with the message's relation, and the namesake gets an entity.
    /// </summary>
    [Fact]
    public async Task Facts_about_a_namesake_the_message_names_exactly_as_the_stored_person_take_the_name_with_its_relation()
    {
        var parent = new Entity
        {
            EntityId = "identity-observation-marta", Name = "Marta", Type = "PERSON", OwnerId = "bruna", Confidence = 1,
            SourceMessageIds = ["m-parents"], CreatedAtUtc = DateTimeOffset.UnixEpoch,
        };
        _messages.GetByIdAsync("m-parents", Arg.Any<CancellationToken>()).Returns(new Message
        {
            MessageId = "m-parents", ConversationId = "c", SessionId = "s", Role = "user",
            Content = "Tomás's parents, Iker and Marta, live in Bilbao.", TimestampUtc = DateTimeOffset.UnixEpoch,
        });
        ShowStoredEntities(parent);
        _replies.Enqueue("""
            {"ops":[
              {"op":"add","kind":"fact","text":"Marta | is | user's boss","quote":"My boss Marta","ref":null},
              {"op":"add","kind":"fact","text":"Marta | put forward | user's name","quote":"put my name forward","ref":null}
            ]}
            """);

        var result = await Write("My boss Marta put my name forward.");

        _calls.Should().ContainSingle("the refusal is deterministic: no correction call");
        result.Facts.Select(f => f.Subject).Should().Equal("Marta, user's boss", "Marta, user's boss");
        var entity = result.Entities.Should().ContainSingle().Which;
        entity.Name.Should().Be("Marta, user's boss");
        entity.Type.Should().Be("PERSON");
        entity.Reference.Should().BeNull();
        result.Outcomes.Should().ContainSingle().Which.ErrorMessage.Should()
            .Contain(nameof(MemoryWriterOps.DropReason.ReferenceRelationConflict)).And.Contain("boss").And.Contain("parent");
    }

    [Fact]
    public async Task Facts_about_the_stored_person_the_message_names_with_their_own_relation_are_left_as_written()
    {
        var parent = new Entity
        {
            EntityId = "identity-observation-marta", Name = "Marta", Type = "PERSON", OwnerId = "bruna", Confidence = 1,
            SourceMessageIds = ["m-parents"], CreatedAtUtc = DateTimeOffset.UnixEpoch,
        };
        _messages.GetByIdAsync("m-parents", Arg.Any<CancellationToken>()).Returns(new Message
        {
            MessageId = "m-parents", ConversationId = "c", SessionId = "s", Role = "user",
            Content = "My mum, Marta, lives in Bilbao.", TimestampUtc = DateTimeOffset.UnixEpoch,
        });
        ShowStoredEntities(parent);
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"fact","text":"Marta | called | user","quote":"My mum Marta called","ref":null}]}""");

        var result = await Write("My mum Marta called me today.");

        result.Facts.Should().ContainSingle().Which.Subject.Should().Be("Marta");
        result.Entities.Should().BeEmpty();
        result.Outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task Facts_that_name_a_stored_person_the_messages_relation_contradicts_keep_the_messages_name()
    {
        ShowStoredDesigner();
        _replies.Enqueue("""
            {"ops":[
              {"op":"add","kind":"fact","text":"Valentina Ríos | started school | this year","quote":"started school this year","ref":null},
              {"op":"add","kind":"fact","text":"Valentina Ríos | is | six years old","quote":"she is six","ref":null}
            ]}
            """);

        var result = await Write("My niece Valentina started school this year, she is six.");

        _calls.Should().ContainSingle("the refusal is deterministic: no correction call");
        result.Facts.Select(f => f.Subject).Should().Equal("Valentina", "Valentina");
        result.Outcomes.Should().ContainSingle().Which.ErrorMessage.Should()
            .Contain(nameof(MemoryWriterOps.DropReason.ReferenceRelationConflict)).And.Contain("niece").And.Contain("designer");
    }

    [Fact]
    public async Task Facts_that_name_a_stored_person_the_turn_before_contradicts_keep_the_messages_name()
    {
        // cover5-4 statement 14.
        ShowStoredDesigner();
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"fact","text":"Valentina Ríos | lost | her first tooth","quote":"Valentina lost her first tooth yesterday","ref":null}]}""");

        var result = await Write("Valentina lost her first tooth yesterday.", true, "My niece Valentina started school this year, she is six.");

        result.Facts.Should().ContainSingle().Which.Subject.Should().Be("Valentina");
    }

    [Theory]
    [InlineData("Valentina sent over the new mock-ups.")]                     // no relation said
    [InlineData("Valentina Ríos sent over the new mock-ups for my niece.")]   // the message names the stored person in full
    public async Task Facts_that_name_a_stored_person_no_relation_contradicts_keep_the_stored_name(string message)
    {
        ShowStoredDesigner();
        _replies.Enqueue("""{"ops":[{"op":"add","kind":"fact","text":"Valentina Ríos | sent over | the new mock-ups","quote":"sent over the new mock-ups","ref":null}]}""");

        var result = await Write(message);

        result.Facts.Should().ContainSingle().Which.Subject.Should().Be("Valentina Ríos");
        result.Outcomes.Should().BeEmpty();
    }

    // ── IDENTITY-THING-REF-LEFT-OUT (the sixth paid check, cover6-1 statement 40: shown "E1 Atlas (OBJECT)", the writer
    // answered "ref": "E1" for "Atlas (OBJECT)". A writer's reference cannot confirm a thing or a place, so the re-mention
    // stayed a pending observation, another identity: the work project "Atlas" made 7 of the check's 23 extra identities.) ──

    [Theory]
    [InlineData("thing-atlas", "Atlas", "OBJECT", "Atlas (OBJECT)", "Atlas is two weeks late.", "Atlas")]
    [InlineData("place-lumen", "Café Lumen", "LOCATION", "Café Lumen (PLACE)", "Café Lumen has live jazz on Fridays.", "Café Lumen")]
    public async Task A_stored_thing_or_place_referred_to_again_is_left_out_and_its_facts_keep_the_stored_name(
        string id, string name, string type, string entity, string message, string quote)
    {
        ShowStored(id, name, type);
        _replies.Enqueue($$"""
            {"ops":[
              {"op":"add","kind":"entity","text":"{{entity}}","quote":"{{quote}}","ref":"E1"},
              {"op":"add","kind":"fact","text":"{{name}} | is | busy","quote":"{{quote}}","ref":null}
            ]}
            """);

        var result = await Write(message);

        _calls.Should().ContainSingle("nothing is left to ask");
        result.Entities.Should().BeEmpty("the same stored thing needs no new observation: its fact carries it");
        result.Facts.Should().ContainSingle().Which.Subject.Should().Be(name);
        result.Outcomes.Should().BeEmpty();
    }

    /// <summary>
    /// The same holds for a correction (the paid check of 24 Nov, ihc1-v1-glm-1 statement 54): a malformed entity text
    /// ("Luna (OBJECT) (also called: Luna) (OBJECT)", "ref": "E1") was asked to be fixed, the fix "Luna (OBJECT)" kept the
    /// ref to the stored thing and was stored, and the re-mention stayed pending as another identity of it.
    /// </summary>
    [Theory]
    [InlineData("thing-atlas", "Atlas", "OBJECT", "Atlas (OBJECT)", "Atlas (OBJECT) (also called: Atlas) (OBJECT)", "Atlas is two weeks late.", "Atlas")]
    [InlineData("place-lumen", "Café Lumen", "LOCATION", "Café Lumen (PLACE)", "Café Lumen (PLACE), the café", "Café Lumen has live jazz on Fridays.", "Café Lumen")]
    public async Task A_stored_thing_or_place_a_correction_refers_to_is_left_out_too(
        string id, string name, string type, string entity, string malformed, string message, string quote)
    {
        ShowStored(id, name, type);
        _replies.Enqueue($$"""
            {"ops":[
              {"op":"add","kind":"entity","text":"{{malformed}}","quote":"{{quote}}","ref":"E1"},
              {"op":"add","kind":"fact","text":"{{name}} | is | busy","quote":"{{quote}}","ref":null}
            ]}
            """);
        _replies.Enqueue($$"""{"ops":[{"op":"add","kind":"entity","text":"{{entity}}","quote":"{{quote}}","ref":"E1"}]}""");

        var result = await Write(message);

        _calls.Should().HaveCount(2, "the malformed entity is asked to be fixed once");
        result.Entities.Should().BeEmpty("the fix names the same stored thing, which needs no new observation");
        result.Facts.Should().ContainSingle().Which.Subject.Should().Be(name);
        result.Outcomes.Should().BeEmpty("the correction answered the issue");
    }

    /// <summary>
    /// IDENTITY-REF-SHORT-FORM-KEPT (the seventh paid check, cover7-2 statement 11): the writer referred "Vale finished the new
    /// checkout mock-ups" to the stored designer under her full name. The reference was right, but "Vale" was never kept,
    /// so at statement 53 a lookup by "Vale" found nobody. A person referred to under the full stored name keeps, as its
    /// other name, a short form of that name its quote uses; a first name alone is part of the name and is not kept.
    /// </summary>
    [Theory]
    [InlineData("Vale finished the new checkout mock-ups today.", "Vale finished the new checkout mock-ups", "Vale")]
    [InlineData("Valentina finished the new checkout mock-ups today.", "Valentina finished the new checkout mock-ups", null)]
    public async Task A_person_referred_to_under_the_full_name_keeps_the_short_form_the_message_used(string message, string quote, string? kept)
    {
        ShowStoredDesigner();
        _replies.Enqueue($$"""{"ops":[{"op":"add","kind":"entity","text":"Valentina Ríos (PERSON)","quote":"{{quote}}","ref":"E1"}]}""");

        var result = await Write(message);

        _calls.Should().ContainSingle();
        var entity = result.Entities.Should().ContainSingle().Which;
        entity.Name.Should().Be("Valentina Ríos");
        entity.Reference!.EntityId.Should().Be(DesignerId);
        if (kept is null) entity.Aliases.Should().BeEmpty();
        else entity.Aliases.Should().Equal(kept);
    }

    private const string SonId = "identity-observation-tomas";

    private void ShowStoredEntities(params Entity[] shown) =>
        _entities.SearchByVectorAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs([.. shown.Select((e, i) => (e, 0.9 - i * 0.01))]);

    private static Entity StoredPerson(string id, string name) => new()
    {
        EntityId = id, Name = name, Type = "PERSON", OwnerId = "bruna", Confidence = 1, SourceMessageIds = ["prior"],
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private const string Parents = "Tomás's parents, Iker and Marta, live in Bilbao.";

    private void ReplyWithTheParents(string? tomasEntity = null) => _replies.Enqueue($$"""
        {"ops":[
          {"op":"add","kind":"entity","text":"Iker (PERSON)","quote":"Iker and Marta","ref":null},
          {"op":"add","kind":"entity","text":"Marta (PERSON)","quote":"Iker and Marta","ref":null},
          {{tomasEntity}}
          {"op":"add","kind":"relationship","text":"Iker -[parent of]-> Tomás","quote":"Tomás's parents, Iker and Marta","ref":null},
          {"op":"add","kind":"relationship","text":"Marta -[parent of]-> Tomás","quote":"Tomás's parents, Iker and Marta","ref":null}
        ]}
        """);

    /// <summary>
    /// IDENTITY-RELATION-GROUNDING: with evidence-based identity a connection reaches the graph only when both its ends are
    /// mentions of the turn, and the writer is told a stored person needs no new mention. So every connection to someone
    /// met before (a partner's parents, a sister's daughter) stayed an unresolved claim: in whos-who-v2's third run, 17 of
    /// the writer's 21 connections; the stored person the writer later had to tell from a namesake was shown without
    /// them. A connection's end that names exactly one stored entity the writer was shown is that entity, referred to in
    /// the connection's own words; identity re-checks the reference as it does the writer's own.
    /// </summary>
    [Fact]
    public async Task A_connection_to_one_stored_person_refers_to_that_person()
    {
        ShowStoredEntities(StoredPerson(SonId, "Tomás"));
        ReplyWithTheParents();

        var result = await Write(Parents);

        var tomas = result.Entities.Should().ContainSingle(e => e.Name == "Tomás").Which;
        tomas.Type.Should().Be("PERSON");
        tomas.Reference!.EntityId.Should().Be(SonId);
        tomas.Reference.Quote.Should().Be("Tomás");
        result.Relationships.Select(r => r.TargetEntity).Should().Equal("Tomás", "Tomás");
        result.Entities.Where(e => e.Name != "Tomás").Should().OnlyContain(e => e.Reference == null);
    }

    [Fact]
    public async Task A_connection_to_a_name_two_stored_people_share_refers_to_neither()
    {
        ShowStoredEntities(StoredPerson(SonId, "Tomás"), StoredPerson("identity-observation-tomas-2", "Tomás"));
        ReplyWithTheParents();

        var result = await Write(Parents);

        result.Entities.Should().NotContain(e => e.Name == "Tomás", "two stored people of that name: which one is not the writer's to guess");
    }

    [Fact]
    public async Task A_connection_to_a_person_the_reply_names_itself_is_left_to_that_mention()
    {
        ShowStoredEntities(StoredPerson(SonId, "Tomás"));
        ReplyWithTheParents("""{"op":"add","kind":"entity","text":"Tomás (PERSON)","quote":"Tomás's parents","ref":null},""");

        var result = await Write(Parents);

        result.Entities.Should().ContainSingle(e => e.Name == "Tomás").Which.Reference.Should().BeNull("the reply's own mention stands");
    }

    [Fact]
    public async Task A_connection_whose_words_do_not_name_the_stored_person_refers_to_nobody()
    {
        ShowStoredEntities(StoredPerson(SonId, "Tomás"));
        _replies.Enqueue("""
            {"ops":[
              {"op":"add","kind":"entity","text":"Iker (PERSON)","quote":"Iker and Marta","ref":null},
              {"op":"add","kind":"relationship","text":"Iker -[parent of]-> Tomás","quote":"Iker and Marta","ref":null}
            ]}
            """);

        var result = await Write(Parents);

        result.Entities.Should().NotContain(e => e.Name == "Tomás");
    }

    /// <summary>
    /// The paid check of 24 Nov (ihc1-v2-glm-1, statement 34): "Our landlord is also called X, no relation" gave the new
    /// landlord and "user's landlord X -[no relation]-> X"; the bare end was referred to the stored brother X, which the
    /// key read as the landlord merged with him. A connection that says there is none, and a bare end beside a namesake the
    /// reply names apart, refer to nobody.
    /// </summary>
    [Theory]
    [InlineData("user's landlord Tomás -[no relation]-> Tomás")]
    [InlineData("user's landlord Tomás -[knows]-> Tomás")]
    public async Task A_bare_end_beside_a_namesake_the_reply_names_apart_refers_to_nobody(string connection)
    {
        ShowStoredEntities(StoredPerson(SonId, "Tomás"));
        _replies.Enqueue($$"""
            {"ops":[
              {"op":"add","kind":"entity","text":"user's landlord Tomás (PERSON)","quote":"Our landlord is also called Tomás","ref":null},
              {"op":"add","kind":"relationship","text":"{{connection}}","quote":"Our landlord is also called Tomás, no relation","ref":null}
            ]}
            """);

        var result = await Write("Our landlord is also called Tomás, no relation, and he fixed the boiler.");

        result.Entities.Should().ContainSingle().Which.Name.Should().Be("user's landlord Tomás");
    }

    /// <summary>
    /// The paid check of 24 Nov (ihc1-v1-glm-1, statement 8): "X is the designer on Atlas" gave a connection to the stored
    /// project and a reference to it; a reference cannot confirm a thing, so it stayed open beside the stored one, a split
    /// the run before the fix never had. Only an end a reference may confirm (a person, an organisation) is referred to.
    /// </summary>
    [Fact]
    public async Task A_connection_to_a_stored_thing_refers_to_nothing()
    {
        ShowStoredEntities(new Entity
        {
            EntityId = "thing-atlas", Name = "Atlas", Type = "OBJECT", OwnerId = "bruna", Confidence = 1, SourceMessageIds = ["prior"],
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        });
        _replies.Enqueue("""
            {"ops":[
              {"op":"add","kind":"entity","text":"Iker (PERSON)","quote":"Iker","ref":null},
              {"op":"add","kind":"relationship","text":"Iker -[designer on]-> Atlas","quote":"Iker is the designer on Atlas","ref":null}
            ]}
            """);

        var result = await Write("Iker is the designer on Atlas.");

        result.Entities.Should().NotContain(e => e.Name == "Atlas");
    }

    [Fact]
    public async Task A_connection_that_says_there_is_none_refers_to_nobody()
    {
        ShowStoredEntities(StoredPerson(SonId, "Tomás"));
        _replies.Enqueue("""
            {"ops":[
              {"op":"add","kind":"entity","text":"Iker (PERSON)","quote":"Iker","ref":null},
              {"op":"add","kind":"relationship","text":"Iker -[not related to]-> Tomás","quote":"Iker is not related to Tomás","ref":null}
            ]}
            """);

        var result = await Write("Iker is not related to Tomás at all.");

        result.Entities.Should().NotContain(e => e.Name == "Tomás");
    }

    /// <summary>
    /// The blind whos-who-v3 run of 24 Nov: "Finn's best mate Sam slept over" (a second Sam; the stored one is someone else)
    /// was written as a fact under the bare name and a connection with no entity of its own; the connection fix referred the
    /// bare end to the one stored Sam, and identity, with no relation of the stored Sam to compare, confirmed the merge in
    /// all three runs. A connection refers only the end the relation is about ("Tomás" in "Tomás's parents"), never an end
    /// the message describes by it, before or after the name, whichever way the writer drew the arrow.
    /// </summary>
    [Theory]
    [InlineData("Joel's best mate Rafa slept over on Saturday.", "Rafa -[best mate of]-> Joel")]
    [InlineData("Joel's best mate Rafa slept over on Saturday.", "Joel -[best mate]-> Rafa")]
    [InlineData("Rafa, Joel's best mate, slept over on Saturday.", "Rafa -[best mate of]-> Joel")]
    [InlineData("My neighbour Rafa lent Joel a ladder on Saturday.", "Rafa -[lent a ladder to]-> Joel")]
    [InlineData("Rafa from the climbing gym lent Joel a ladder on Saturday.", "Rafa -[lent a ladder to]-> Joel")]
    public async Task A_connection_never_refers_an_end_the_message_describes_by_it(string message, string connection)
    {
        ShowStoredEntities(StoredPerson("identity-observation-rafa", "Rafa"), StoredPerson("identity-observation-joel", "Joel"));
        _replies.Enqueue($$"""
            {"ops":[
              {"op":"add","kind":"fact","text":"Rafa | slept over at | user's home","quote":"{{message}}","ref":null},
              {"op":"add","kind":"relationship","text":"{{connection}}","quote":"{{message}}","ref":null}
            ]}
            """);

        var result = await Write(message);

        result.Entities.Should().NotContain(e => e.Name == "Rafa", "a person the message introduces by a relation is not the stored namesake on that word alone");
        if (connection.Contains("Joel"))
            result.Entities.Should().ContainSingle(e => e.Name == "Joel").Which.Reference!.EntityId.Should().Be("identity-observation-joel",
                "the person the relation is about is still referred");
    }

    [Theory]
    [InlineData("That's what Rafa told Joel about the ladder.")]
    [InlineData("It's Rafa who lent Joel the ladder.")]
    public async Task A_contraction_is_no_owner_and_the_stored_person_is_still_referred(string message)
    {
        ShowStoredEntities(StoredPerson("identity-observation-rafa", "Rafa"), StoredPerson("identity-observation-joel", "Joel"));
        _replies.Enqueue($$"""
            {"ops":[
              {"op":"add","kind":"relationship","text":"Rafa -[lent a ladder to]-> Joel","quote":"{{message}}","ref":null}
            ]}
            """);

        var result = await Write(message);

        result.Entities.Should().ContainSingle(e => e.Name == "Rafa").Which.Reference!.EntityId.Should().Be("identity-observation-rafa");
    }

    [Fact]
    public async Task The_default_writer_adds_no_reference_to_a_connections_end()
    {
        ShowStoredEntities(StoredPerson(SonId, "Tomás"));
        _replies.Enqueue("""
            {"ops":[
              {"op":"add","kind":"entity","text":"Iker (PERSON)","quote":"Iker and Marta"},
              {"op":"add","kind":"relationship","text":"Iker -[parent of]-> Tomás","quote":"Tomás's parents, Iker and Marta"}
            ]}
            """);

        var result = await Write(Parents, eventIdentities: false);

        result.Entities.Should().NotContain(e => e.Name == "Tomás", "off is byte-identical");
    }
}
