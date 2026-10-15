using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.McpHost.Coding;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Text.Json;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingWriterTests
{
    private static readonly CodingIdentity Identity = new("owner", "alpha");
    private static CodingRememberRequest Request => new()
    {
        Session = "session", TurnId = "turn-1", UserText = "We use port 5234.",
        AssistantText = "Understood.", Commit = new string('a', 40), Source = "src/settings.cs",
    };

    [Fact]
    public async Task Secret_content_is_rejected_before_source_lookup_or_embeddings()
    {
        await using var fixture = new Fixture();
        var run = () => fixture.Writer.WriteAsync(Identity, Request with { AssistantText = "canary-credential-value" });
        await run.Should().ThrowAsync<ArgumentException>();
        fixture.Messages.ReceivedCalls().Should().BeEmpty();
        fixture.Ingestion.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Recognizable_secret_in_prior_context_stops_ingestion_before_new_sources()
    {
        await using var fixture = new Fixture();
        var prior = Fixture.Existing("prior", Request) with { Content = "canary-credential-value" };
        fixture.ShortTerm.GetRecentMessagesAsync(Arg.Any<string>(), 6, Arg.Any<CancellationToken>()).Returns(new[] { prior });
        var run = () => fixture.Writer.WriteAsync(Identity, Request);
        await run.Should().ThrowAsync<ArgumentException>();
        fixture.Ingestion.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Persists_owned_sources_before_calling_the_writer_with_one_user_target()
    {
        await using var fixture = new Fixture();
        var order = new List<string>();
        fixture.ShortTerm.AddConversationAsync(Arg.Any<string>(), Arg.Any<string>(), "owner",
            Arg.Any<IReadOnlyDictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("conversation"); return (Conversation)null!; });
        fixture.Ingestion.AddMessageWithIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(ci => { order.Add(ci.ArgAt<string>(2)); return Fixture.MessageFrom(ci); });
        ExtractionRequest? captured = null;
        fixture.Ingestion.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => { order.Add("extract"); captured = ci.Arg<ExtractionRequest>(); return new ExtractionResult(); });
        using var stop = new CancellationTokenSource();
        var result = await fixture.Writer.WriteAsync(Identity, Request, stop.Token);
        order.Should().Equal("conversation", "user", "assistant", "extract");
        captured.Should().NotBeNull();
        captured!.InputMode.Should().Be(ExtractionInputMode.ConversationTurn);
        captured.UserId.Should().Be("owner");
        captured.ShareWithEveryone.Should().BeFalse();
        captured.DeferIfOnlyAsking.Should().BeFalse();
        captured.TrustLevel.Should().Be(MemoryTrustLevel.Untrusted);
        captured.Messages.Select(m => m.Role).Should().Equal("user", "assistant");
        captured.Messages[0].Content.Should().Be(Request.UserText);
        captured.Messages[0].Metadata["coding_commit"].Should().Be(Request.Commit);
        captured.Messages[0].Metadata["coding_source"].Should().Be(Request.Source);
        captured.Messages[0].Metadata["coding_payload_sha256"].Should().Be(Request.PayloadHash());
        captured.SessionId.Should().NotBe(Request.Session);
        result.SourceMessageId.Should().Be(captured.Messages[0].MessageId);
        result.Status.Should().Be(IngestionStatus.Succeeded);
        await fixture.Ingestion.Received(1).ExtractAndPersistAsync(captured, stop.Token);
    }

    [Fact]
    public async Task Existing_source_is_an_unknown_prior_attempt_not_a_success_or_automatic_replay()
    {
        await using var fixture = new Fixture();
        fixture.Messages.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            Fixture.Existing(ci.ArgAt<string>(0), Request));
        var result = await fixture.Writer.WriteAsync(Identity, Request);
        result.PriorAttemptUnknown.Should().BeTrue();
        result.Status.Should().BeNull();
        result.ExtractionPath.Should().Be(ExtractionPath.Unknown);
        fixture.Ingestion.ReceivedCalls().Should().BeEmpty();
        fixture.ShortTerm.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Reusing_a_turn_id_for_changed_content_is_rejected_before_any_write()
    {
        await using var fixture = new Fixture();
        fixture.Messages.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            Fixture.Existing(ci.ArgAt<string>(0), Request));
        var run = () => fixture.Writer.WriteAsync(Identity, Request with { UserText = "Now use port 6000." });
        await run.Should().ThrowAsync<InvalidOperationException>();
        fixture.Ingestion.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(IngestionStatus.PartiallySucceeded)]
    [InlineData(IngestionStatus.Failed)]
    public async Task Partial_and_failed_ingestion_are_not_reported_as_success(IngestionStatus status)
    {
        await using var fixture = new Fixture();
        fixture.Ingestion.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult { Status = status });
        var result = await fixture.Writer.WriteAsync(Identity, Request);
        result.Status.Should().Be(status);
        result.PriorAttemptUnknown.Should().BeFalse();
    }

    [Fact]
    public async Task An_unavailable_writer_cannot_leave_source_records_or_fall_back_to_extractors()
    {
        await using var fixture = new Fixture();
        fixture.MemoryWriter.IsEnabled.Returns(false);
        var run = () => fixture.Writer.WriteAsync(Identity, Request);
        await run.Should().ThrowAsync<InvalidOperationException>();
        fixture.Ingestion.ReceivedCalls().Should().BeEmpty();
        fixture.ShortTerm.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Extractor_fallback_configuration_is_rejected_before_writing_sources()
    {
        await using var fixture = new Fixture(extractorFallback: true);
        var run = () => fixture.Writer.WriteAsync(Identity, Request);
        await run.Should().ThrowAsync<InvalidOperationException>();
        fixture.Ingestion.ReceivedCalls().Should().BeEmpty();
        fixture.ShortTerm.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(IngestionStatus.Succeeded)]
    [InlineData(IngestionStatus.PartiallySucceeded)]
    [InlineData(IngestionStatus.Failed)]
    public async Task Explicit_fallback_permission_preserves_actual_path_and_status(IngestionStatus status)
    {
        await using var fixture = new Fixture(extractorFallback: true, allowFallback: true);
        fixture.Ingestion.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult { Status = status, Path = ExtractionPath.WriterFallback });
        var result = await fixture.Writer.WriteAsync(Identity, Request);
        result.ExtractionPath.Should().Be(ExtractionPath.WriterFallback);
        result.Status.Should().Be(status);
        await fixture.Ingestion.Received(1).ExtractAndPersistAsync(Arg.Is<ExtractionRequest>(r =>
            r.InputMode == ExtractionInputMode.ConversationTurn && r.TrustLevel == MemoryTrustLevel.Untrusted &&
            r.Messages[0].Metadata["coding_payload_sha256"].Equals(Request.PayloadHash())), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ExtractionPath.Unknown)]
    [InlineData(ExtractionPath.Skipped)]
    [InlineData(ExtractionPath.Writer)]
    public async Task Receipt_uses_reported_path_not_configured_writer(ExtractionPath path)
    {
        await using var fixture = new Fixture();
        fixture.Ingestion.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult { Path = path });
        (await fixture.Writer.WriteAsync(Identity, Request)).ExtractionPath.Should().Be(path);
    }

    [Fact]
    public async Task Persisted_json_metadata_and_forgotten_sources_do_not_trigger_replay()
    {
        await using var fixture = new Fixture();
        fixture.Messages.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            Fixture.Existing(ci.ArgAt<string>(0), Request) with
            {
                InvalidatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    JsonSerializer.Serialize(new { coding_payload_sha256 = Request.PayloadHash() }))!,
            });
        var result = await fixture.Writer.WriteAsync(Identity, Request);
        result.PriorAttemptUnknown.Should().BeTrue();
        fixture.Ingestion.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Earlier_context_is_bounded_and_separate_from_the_current_targets()
    {
        await using var fixture = new Fixture();
        var recent = Fixture.Existing("prior-user", Request) with { Content = "earlier user" };
        var reply = recent with { MessageId = "prior-reply", Role = "assistant", Content = "earlier reply" };
        var older = recent with { MessageId = "older", Content = new string('x', 8000) };
        fixture.ShortTerm.GetRecentMessagesAsync(Arg.Any<string>(), 6, Arg.Any<CancellationToken>())
            .Returns(new[] { reply, recent, older });
        await fixture.Writer.WriteAsync(Identity, Request);
        await fixture.Ingestion.Received(1).ExtractAndPersistAsync(Arg.Is<ExtractionRequest>(r =>
            r.ContextMessages.Count == 2 && r.ContextMessages[0] == recent && r.ContextMessages[1] == reply &&
            r.Messages.All(m => m.MessageId != recent.MessageId && m.MessageId != reply.MessageId)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("short", "source", "text")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "https://example.test/?secret=value", "text")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "source", "")]
    public async Task Invalid_input_stops_before_any_repository_or_model_call(string commit, string source, string text)
    {
        await using var fixture = new Fixture();
        var run = () => fixture.Writer.WriteAsync(Identity, Request with { Commit = commit, Source = source, UserText = text });
        await run.Should().ThrowAsync<ArgumentException>();
        fixture.Messages.ReceivedCalls().Should().BeEmpty();
        fixture.Ingestion.ReceivedCalls().Should().BeEmpty();
        fixture.ShortTerm.ReceivedCalls().Should().BeEmpty();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public IMemoryIngestion Ingestion { get; } = Substitute.For<IMemoryIngestion>();
        public IMessageRepository Messages { get; } = Substitute.For<IMessageRepository>();
        public IShortTermMemoryService ShortTerm { get; } = Substitute.For<IShortTermMemoryService>();
        public IMemoryWriter MemoryWriter { get; } = Substitute.For<IMemoryWriter>();
        public CodingWriter Writer { get; }
        private readonly ServiceProvider _services;

        public Fixture(bool extractorFallback = false, bool allowFallback = false)
        {
            MemoryWriter.IsEnabled.Returns(true);
            ShortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<Message>());
            Ingestion.AddMessageWithIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>(), Arg.Any<CancellationToken>())
                .Returns(MessageFrom);
            Ingestion.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>()).Returns(new ExtractionResult());
            _services = new ServiceCollection().AddSingleton(Ingestion).AddSingleton(Messages).AddSingleton(ShortTerm)
                .AddSingleton(new CodingServiceOptions { OwnerId = "owner", Projects = new Dictionary<string, string> { ["alpha"] = "TOKEN" }, AllowExtractorFallback = allowFallback })
                .AddSingleton(MemoryWriter).AddSingleton<IWritableMemoryOwnerContext, DefaultMemoryOwnerContext>()
                .AddSingleton<IWritableMemoryStoreContext, DefaultMemoryStoreContext>()
                .AddSingleton(Options.Create(new ExtractionOptions { FallBackToExtractorsWhenWriterFails = extractorFallback }))
                .AddSingleton(Options.Create(new MemoryStoreOptions
                {
                    Strategy = MemoryStorageStrategy.DatabasePerApplication, DatabasePrefix = "coding-", AutoProvision = false,
                })).BuildServiceProvider();
            Writer = new CodingWriter(new CodingOperationScope(_services.GetRequiredService<IServiceScopeFactory>()), new(["canary-credential-value"]));
        }

        public static Message MessageFrom(NSubstitute.Core.CallInfo ci) => new()
        {
            SessionId = ci.ArgAt<string>(0), ConversationId = ci.ArgAt<string>(1), Role = ci.ArgAt<string>(2),
            Content = ci.ArgAt<string>(3), MessageId = ci.ArgAt<string>(4), TimestampUtc = DateTimeOffset.UtcNow,
            Metadata = ci.ArgAt<IReadOnlyDictionary<string, object>>(5),
        };

        public static Message Existing(string id, CodingRememberRequest request) => new()
        {
            SessionId = CodingInput.SessionId(Identity, request.Session),
            ConversationId = CodingInput.SessionId(Identity, request.Session), Role = "user", Content = request.UserText,
            MessageId = id, TimestampUtc = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object> { ["coding_payload_sha256"] = request.PayloadHash() },
        };
        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }
}
