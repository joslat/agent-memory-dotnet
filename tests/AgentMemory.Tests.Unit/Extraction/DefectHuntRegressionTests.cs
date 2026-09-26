using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Core;
using AgentMemory.Core.Resolution;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Regressions for the whole-branch defect hunt (2026-09-26): failures that were swallowed without a
/// trace, a configuration that silently defeated "never guess", and a relational name taken for a person.
/// </summary>
[Collection("Observability")]
public sealed class DefectHuntRegressionTests
{
    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _spans = [];

        public SpanCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == AgentMemoryDiagnostics.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => { lock (_spans) _spans.Add(a); },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public List<Activity> Named(string name)
        {
            lock (_spans) return _spans.Where(s => s.OperationName == name).ToList();
        }

        public void Dispose() => _listener.Dispose();
    }

    // ---- L1: a failed extraction is visible on its span ----

    private sealed class TestAgentSession : AgentSession;

    private sealed class StubAgent : AIAgent
    {
        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session,
            AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
            AgentSession? session, AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(System.Text.Json.JsonElement serializedState,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(AgentSession session,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_failed_extraction_marks_its_span_as_an_error()
    {
        using var capture = new SpanCapture();
        var memory = Substitute.For<IMemoryService>();
        memory.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ExtractionResult>>(_ => throw new FormatException("attempts exhausted"));
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        var provider = new Neo4jMemoryContextProvider(
            memory, Substitute.For<IEmbeddingOrchestrator>(), Substitute.For<IClock>(), ids,
            Options.Create(new MemoryOptions()), Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions()), NullLogger<Neo4jMemoryContextProvider>.Instance);
        var session = new TestAgentSession();
        session.WithMemoryIdentity(userId: "alice", sessionId: "s1", conversationId: "c1");

#pragma warning disable MAAI001
        await provider.InvokedAsync(new AIContextProvider.InvokedContext(new StubAgent(), session,
            [new ChatMessage(ChatRole.User, "Alice works at Acme.")], [new ChatMessage(ChatRole.Assistant, "Noted.")]),
            CancellationToken.None);
#pragma warning restore MAAI001

        var extract = capture.Named("memory.store.extract").Should().ContainSingle().Subject;
        extract.Status.Should().Be(ActivityStatusCode.Error, "the turn succeeded, but the extraction did not, and the trace must say so");
        extract.GetTagItem(MemoryTelemetry.ErrorType).Should().Be(typeof(FormatException).FullName);
    }

    // ---- L5: a transport failure is visible on its attempt span ----

    private sealed class FailingChat : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("host rejected the request");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task A_transport_failure_marks_its_attempt_span_as_an_error()
    {
        using var capture = new SpanCapture();
        var extractor = new LlmUnifiedMemoryExtractor(new FailingChat(),
            Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true, MaxRetries = 0 }),
            NullLogger<LlmUnifiedMemoryExtractor>.Instance);

        try
        {
            await extractor.ExtractAsync([new Message
            {
                MessageId = "m", ConversationId = "c", SessionId = "s", Role = "user",
                Content = "Alice works at Acme.", TimestampUtc = DateTimeOffset.UtcNow,
            }]);
        }
        catch (InvalidOperationException) { /* the extractor may surface it; the span is what is tested */ }

        var attempt = capture.Named("memory.extract.attempt").Should().ContainSingle().Subject;
        attempt.Status.Should().Be(ActivityStatusCode.Error);
        attempt.GetTagItem(MemoryTelemetry.ErrorType).Should().Be(typeof(InvalidOperationException).FullName);
    }

    // ---- L2: partial names must never auto-merge ----

    [Fact]
    public void A_partial_name_confidence_at_the_auto_merge_threshold_fails_validation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryCore(o =>
        {
            o.Extraction.EntityResolution.EnablePartialNameMatch = true;
            o.Extraction.EnableAutoMerge = true;
            o.Extraction.AutoMergeThreshold = 0.95;
            o.Extraction.EntityResolution.PartialNameMatchConfidence = 0.95;
        });
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<MemoryOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*PartialNameMatchConfidence must be below AutoMergeThreshold*");
    }

    // ---- L3: "Priya's mom" is not Priya ----

    [Fact]
    public async Task A_relational_name_is_never_a_partial_match()
    {
        var matcher = new PartialNameEntityMatcher(new EntityResolutionOptions { EnablePartialNameMatch = true },
            NullLogger.Instance);
        var mom = new Entity
        {
            EntityId = "m1", Name = "Priya's mom", Type = "PERSON", Confidence = 1,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        };

        var result = await matcher.TryMatchAsync(new ExtractedEntity { Name = "Priya", Type = "PERSON" }, [mom]);

        result.Should().BeNull("\"Priya's mom\" describes someone through Priya; it is not Priya");
    }
}
