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
    private readonly ServiceProvider _services;

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
        var relationships = Substitute.For<IRelationshipRepository>();
        relationships.GetLiveAroundAsync(default!, default!, default, default, default, default).ReturnsForAnyArgs([]);
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        _services = new ServiceCollection().AddSingleton(facts).AddSingleton(preferences).AddSingleton(_entities)
            .AddSingleton(relationships).AddSingleton(embeddings).BuildServiceProvider();
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _calls.Add((call.Arg<IEnumerable<ChatMessage>>().ToList(), call.Arg<ChatOptions?>()));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, _replies.Count > 0 ? _replies.Dequeue() : "{\"ops\":[]}"));
        });
    }

    public void Dispose() => _services.Dispose();

    private void ShowStoredGoLive() =>
        _entities.SearchByVectorAsync(default!, default, default, default, default).ReturnsForAnyArgs([(GoLive, 0.9)]);

    private Task<UnifiedExtractionResult> Write(string text, bool eventIdentities = true)
    {
        var writer = new LlmMemoryWriter(_chat, Options.Create(new LlmExtractionOptions
        { UseMemoryWriter = true, MemoryWriterEventIdentities = eventIdentities }), _services, NullLogger<LlmMemoryWriter>.Instance);
        return writer.WriteAsync(new(ExtractionWindow.ForTargets([new() { MessageId = "current", ConversationId = "c",
            SessionId = "s", Role = "user", Content = text, TimestampUtc = DateTimeOffset.UnixEpoch }]), MemoryScope.For("bruna", false)));
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
}
