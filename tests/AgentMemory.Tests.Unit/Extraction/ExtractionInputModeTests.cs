using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Extraction.Llm;
using Microsoft.Extensions.AI;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

public sealed class ExtractionInputModeTests
{
    private readonly IMemoryWriter _writer = Substitute.For<IMemoryWriter>();
    private readonly IUnifiedMemoryExtractor _extractor = Substitute.For<IUnifiedMemoryExtractor>();
    private readonly IPersistenceStage _persistence = Substitute.For<IPersistenceStage>();
    private readonly IMultiSessionUnifiedMemoryExtractor _batch = Substitute.For<IMultiSessionUnifiedMemoryExtractor>();

    public ExtractionInputModeTests()
    {
        _writer.IsEnabled.Returns(true);
        _writer.WriteAsync(Arg.Any<MemoryWriteRequest>(), Arg.Any<CancellationToken>()).Returns(new UnifiedExtractionResult());
        _extractor.IsEnabled.Returns(true);
        _extractor.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>()).Returns(new UnifiedExtractionResult());
        _extractor.ExtractWithContextAsync(Arg.Any<ExtractionWindow>(), Arg.Any<CancellationToken>()).Returns(new UnifiedExtractionResult());
        _persistence.PersistAsync(Arg.Any<ExtractionStageResult>(), Arg.Any<string?>(), Arg.Any<MemoryTrustLevel>(), Arg.Any<CancellationToken>())
            .Returns(call => new PersistenceResult { Outcomes = call.Arg<ExtractionStageResult>().Outcomes });
        _batch.IsEnabled.Returns(true);
        _batch.ExtractAsync(Arg.Any<IReadOnlyList<ExtractionRequest>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyList<ExtractionRequest>>(0).ToDictionary(r => r.SessionId, _ => new UnifiedExtractionResult()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_of_one_user_message_bypasses_writer_and_preserves_context(bool context)
    {
        var request = Request(ExtractionInputMode.Import) with { ContextMessages = context ? [Message("earlier")] : [] };
        var result = await Pipeline().ExtractAsync(request);
        await _writer.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
        if (context)
            await _extractor.Received(1).ExtractWithContextAsync(Arg.Is<ExtractionWindow>(w =>
                ReferenceEquals(w.Targets, request.Messages) && ReferenceEquals(w.Context, request.ContextMessages)), Arg.Any<CancellationToken>());
        else
            await _extractor.Received(1).ExtractAsync(request.Messages, Arg.Any<CancellationToken>());
        result.SourceMessageIds.Should().Equal("target");
        result.Path.Should().Be(ExtractionPath.Extractors);
    }

    [Theory]
    [InlineData(ExtractionInputMode.Unspecified)]
    [InlineData(ExtractionInputMode.ConversationTurn)]
    public async Task Conversation_keeps_writer_window_scope_and_cancellation(ExtractionInputMode mode)
    {
        using var stop = new CancellationTokenSource();
        var request = Request(mode) with { ContextMessages = [Message("earlier"), Message("older")] };
        var result = await Pipeline().ExtractAsync(request, stop.Token);
        result.Path.Should().Be(ExtractionPath.Writer);
        await _writer.Received(1).WriteAsync(Arg.Is<MemoryWriteRequest>(r =>
            ReferenceEquals(r.Window.Targets, request.Messages) && ReferenceEquals(r.Window.Context, request.ContextMessages)
            && r.Scope!.OwnerId == "owner"), stop.Token);
        _extractor.ReceivedCalls().Should().NotContain(c => c.GetMethodInfo().Name.StartsWith("Extract"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Conversation_rejects_ambiguous_user_targets_before_providers(int count)
    {
        var request = Request(ExtractionInputMode.ConversationTurn) with { Messages = Enumerable.Range(0, count).Select(i => Message(i.ToString())).ToArray() };
        await Pipeline().Invoking(p => p.ExtractAsync(request)).Should().ThrowAsync<ArgumentException>();
        AssertNoWork();
    }

    [Theory]
    [InlineData(ExtractionInputMode.ConversationTurn)]
    [InlineData(ExtractionInputMode.Import)]
    public async Task Explicit_mode_rejects_turn_deferral_before_providers(ExtractionInputMode mode)
    {
        await Pipeline().Invoking(p => p.ExtractAsync(Request(mode) with { DeferIfOnlyAsking = true }))
            .Should().ThrowAsync<ArgumentException>();
        AssertNoWork();
    }

    [Theory]
    [InlineData(ExtractionInputMode.ConversationTurn)]
    [InlineData(ExtractionInputMode.Import)]
    public async Task Explicit_mode_without_its_enabled_provider_fails_clearly(ExtractionInputMode mode)
    {
        if (mode == ExtractionInputMode.ConversationTurn) _writer.IsEnabled.Returns(false);
        else _extractor.IsEnabled.Returns(false);
        await Pipeline().Invoking(p => p.ExtractAsync(Request(mode))).Should().ThrowAsync<InvalidOperationException>();
        AssertNoWork();
    }

    [Fact]
    public async Task Unknown_mode_fails_before_providers()
    {
        await Pipeline().Invoking(p => p.ExtractAsync(Request((ExtractionInputMode)99))).Should().ThrowAsync<ArgumentOutOfRangeException>();
        AssertNoWork();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multi_session_path_rejects_live_turn_before_any_request_runs(bool enabled)
    {
        _batch.IsEnabled.Returns(enabled);
        var requests = new[] { Request(ExtractionInputMode.Import) with { SessionId = "first" }, Request(ExtractionInputMode.ConversationTurn) };
        await Pipeline().Invoking(p => p.ExtractBatchAsync(requests, 2, 1000)).Should().ThrowAsync<ArgumentException>();
        AssertNoWork();
        await _batch.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default, default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_batch_keeps_batch_limits_and_never_calls_writer(bool enabled)
    {
        _batch.IsEnabled.Returns(enabled);
        var request = Request(ExtractionInputMode.Import);
        var results = await Pipeline().ExtractBatchAsync([request], 2, 1000);
        results.Single().Path.Should().Be(enabled ? ExtractionPath.PreExtracted : ExtractionPath.Extractors);
        await _writer.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
        if (enabled)
            await _batch.Received(1).ExtractAsync(Arg.Is<IReadOnlyList<ExtractionRequest>>(r => r.Single() == request), 2, 1000, Arg.Any<CancellationToken>());
        else
            await _extractor.Received(1).ExtractAsync(request.Messages, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Core_no_op_stub_is_not_an_import_provider()
    {
        _extractor.IsEnabled.Returns(false);
        var stub = new StubFactExtractor(NullLogger<StubFactExtractor>.Instance);
        await Pipeline(facts: [stub]).Invoking(p => p.ExtractAsync(Request(ExtractionInputMode.Import)))
            .Should().ThrowAsync<InvalidOperationException>();
        AssertNoWork();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conversation_preserves_configured_writer_failure_fallback(bool fallback)
    {
        _writer.WriteAsync(Arg.Any<MemoryWriteRequest>(), Arg.Any<CancellationToken>())
            .Returns<UnifiedExtractionResult>(_ => throw new InvalidOperationException("writer unavailable"));
        var result = await Pipeline(fallback: fallback).ExtractAsync(Request(ExtractionInputMode.ConversationTurn));
        result.Path.Should().Be(fallback ? ExtractionPath.WriterFallback : ExtractionPath.Writer);
        result.Status.Should().Be(fallback ? IngestionStatus.Succeeded : IngestionStatus.Failed);
        await _extractor.Received(fallback ? 1 : 0).ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
        await _persistence.Received(1).PersistAsync(Arg.Is<ExtractionStageResult>(s => fallback
            ? !s.WrittenByWriter && s.WriterFallbackReason != null
            : s.Outcomes.Any(o => o.Status == IngestionItemStatus.Failed)), "owner", Arg.Any<MemoryTrustLevel>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Conversation_failure_without_an_extractor_retains_the_failure()
    {
        _extractor.IsEnabled.Returns(false);
        _writer.WriteAsync(Arg.Any<MemoryWriteRequest>(), Arg.Any<CancellationToken>())
            .Returns<UnifiedExtractionResult>(_ => throw new InvalidOperationException("writer unavailable"));
        var result = await Pipeline().ExtractAsync(Request(ExtractionInputMode.ConversationTurn));
        result.Path.Should().Be(ExtractionPath.Writer);
        result.Status.Should().Be(IngestionStatus.Failed);
        await _persistence.Received(1).PersistAsync(Arg.Is<ExtractionStageResult>(s =>
            s.Outcomes.Any(o => o.Status == IngestionItemStatus.Failed)), "owner", Arg.Any<MemoryTrustLevel>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_fallback_is_reported_as_failed_with_its_actual_path()
    {
        _writer.WriteAsync(Arg.Any<MemoryWriteRequest>(), Arg.Any<CancellationToken>())
            .Returns<UnifiedExtractionResult>(_ => throw new InvalidOperationException("writer unavailable"));
        _extractor.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns<UnifiedExtractionResult>(_ => throw new InvalidOperationException("extractor unavailable"));
        var result = await Pipeline().ExtractAsync(Request(ExtractionInputMode.ConversationTurn));
        result.Path.Should().Be(ExtractionPath.WriterFallback);
        result.Status.Should().Be(IngestionStatus.Failed);
        result.SourceMessageIds.Should().Equal("target");
    }

    [Fact]
    public async Task Cancellation_is_not_an_outage_to_route_around()
    {
        using var stop = new CancellationTokenSource();
        _writer.WriteAsync(Arg.Any<MemoryWriteRequest>(), Arg.Any<CancellationToken>())
            .Returns<UnifiedExtractionResult>(_ => { stop.Cancel(); throw new OperationCanceledException(stop.Token); });
        await Pipeline().Invoking(p => p.ExtractAsync(Request(ExtractionInputMode.ConversationTurn), stop.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        _extractor.ReceivedCalls().Should().NotContain(c => c.GetMethodInfo().Name.StartsWith("Extract"));
        _persistence.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(ExtractionInputMode.Unspecified)]
    [InlineData(ExtractionInputMode.Import)]
    public async Task Per_kind_custom_prompt_and_payload_bytes_match_existing_entry_point(ExtractionInputMode mode)
    {
        _writer.IsEnabled.Returns(false);
        _extractor.IsEnabled.Returns(false);
        var client = Substitute.For<IChatClient>();
        var payloads = new List<string>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                payloads.Add(System.Text.Json.JsonSerializer.Serialize(call.ArgAt<IEnumerable<ChatMessage>>(0)
                    .Select(m => new { Role = m.Role.Value, m.Text })));
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"facts\": []}"));
            });
        var extractor = new LlmFactExtractor(client, Options.Create(new LlmExtractionOptions { FactExtractionPrompt = "Host's custom fact prompt." }),
            NullLogger<LlmFactExtractor>.Instance);
        var request = Request(mode) with { TypesToExtract = ExtractionTypes.Facts };
        await extractor.ExtractAsync(request.Messages);
        await Pipeline(facts: [extractor]).ExtractAsync(request);
        payloads.Should().HaveCount(2);
        payloads[1].Should().Be(payloads[0]).And.Contain("custom fact prompt");
    }

    [Theory]
    [InlineData(ExtractionInputMode.ConversationTurn)]
    [InlineData(ExtractionInputMode.Import)]
    public async Task No_requested_types_does_not_require_or_call_providers(ExtractionInputMode mode)
    {
        _writer.IsEnabled.Returns(false);
        _extractor.IsEnabled.Returns(false);
        var result = await Pipeline().ExtractAsync(Request(mode) with { TypesToExtract = ExtractionTypes.None });
        result.Path.Should().Be(ExtractionPath.Skipped);
        await _writer.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
        _extractor.ReceivedCalls().Should().NotContain(c => c.GetMethodInfo().Name.StartsWith("Extract"));
    }

    private void AssertNoWork()
    {
        _writer.ReceivedCalls().Should().NotContain(c => c.GetMethodInfo().Name == "WriteAsync");
        _extractor.ReceivedCalls().Should().NotContain(c => c.GetMethodInfo().Name.StartsWith("Extract"));
        _persistence.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(ExtractionInputMode.ConversationTurn)]
    [InlineData(ExtractionInputMode.Import)]
    public async Task Explicit_mode_preserves_owner_and_trust(ExtractionInputMode mode)
    {
        await Pipeline().ExtractAsync(Request(mode) with { TrustLevel = MemoryTrustLevel.ApplicationTrusted });
        await _persistence.Received(1).PersistAsync(Arg.Any<ExtractionStageResult>(), "owner",
            MemoryTrustLevel.ApplicationTrusted, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ExtractionInputMode.ConversationTurn)]
    [InlineData(ExtractionInputMode.Import)]
    public async Task Contradictory_shared_owner_fails_before_providers(ExtractionInputMode mode)
    {
        await Pipeline().Invoking(p => p.ExtractAsync(Request(mode) with { ShareWithEveryone = true }))
            .Should().ThrowAsync<ArgumentException>();
        AssertNoWork();
    }

    [Fact]
    public async Task Conversation_accepts_other_role_targets_and_case_insensitive_user_role()
    {
        await Pipeline().ExtractAsync(Request(ExtractionInputMode.ConversationTurn) with
        {
            Messages = [Message("target") with { Role = "USER" }, Message("reply") with { Role = "assistant" }],
        });
        await _writer.Received(1).WriteAsync(Arg.Is<MemoryWriteRequest>(r => r.Window.Targets.Count == 2), Arg.Any<CancellationToken>());
    }

    private MemoryExtractionPipeline Pipeline(IFactExtractor[]? facts = null, bool fallback = true) => new(
        new ExtractionStage([], facts ?? [], [], [], [_extractor], Substitute.For<IEntityResolver>(),
            Options.Create(new ExtractionOptions { FallBackToExtractorsWhenWriterFails = fallback }),
            NullLogger<ExtractionStage>.Instance, [_writer]), _persistence, NullLogger<MemoryExtractionPipeline>.Instance,
        new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions()), NullLogger<DefaultMemoryIsolationPolicy>.Instance),
        multiSessionExtractors: [_batch]);

    private static ExtractionRequest Request(ExtractionInputMode mode) => new()
    {
        SessionId = "session", UserId = "owner", InputMode = mode, Messages = [Message("target")],
    };

    private static Message Message(string id) => new()
    {
        MessageId = id, SessionId = "session", ConversationId = "conversation", Role = "user",
        Content = "My sister lives in Madrid.", TimestampUtc = DateTimeOffset.UnixEpoch,
    };
}
