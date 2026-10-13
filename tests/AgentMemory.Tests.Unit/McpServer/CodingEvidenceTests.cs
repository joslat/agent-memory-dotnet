using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpHost.Coding;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingEvidenceTests
{
    private static readonly CodingIdentity Owner = new("owner", "alpha");

    [Fact]
    public async Task Preview_has_no_memory_prose_and_only_selected_items_can_expand()
    {
        await using var f = new Fixture();
        var preview = await f.Preview();
        JsonSerializer.Serialize(preview).Should().NotContain("private-fact-canary").And.NotContain("private-source-canary");
        preview.GetProperty("items")[0].GetProperty("itemId").GetString().Should().Be("f1");
        var expanded = await f.Expand(preview.GetProperty("previewId").GetString()!);
        Json(expanded).GetProperty("items")[0].GetProperty("text").GetString().Should().Be("private-fact-canary");
        f.History.ClearReceivedCalls();
        var unselected = await f.Expand(preview.GetProperty("previewId").GetString()!, id: "unselected");
        Json(unselected).GetProperty("items")[0].GetProperty("status").GetString().Should().Be("unavailable");
        f.History.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Old_preview_cannot_expand_a_closed_parent_or_its_source(bool source)
    {
        await using var f = new Fixture(); var preview = await f.Preview();
        f.Row = f.Row with { Status = MemoryHistoryStatus.Invalidated, InvalidatedAtUtc = DateTimeOffset.UtcNow };
        var result = await f.Expand(preview.GetProperty("previewId").GetString()!, source: source);
        Json(result).GetProperty("items")[0].GetProperty("status").GetString().Should().Be("unavailable");
        JsonSerializer.Serialize(result.Body).Should().NotContain("private-");
        f.Messages.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_forgotten_source_or_foreign_conversation_is_not_expanded()
    {
        await using var f = new Fixture(); var preview = await f.Preview();
        var id = preview.GetProperty("previewId").GetString()!;
        f.Source = f.Source with { InvalidatedAtUtc = DateTimeOffset.UtcNow };
        Json(await f.Expand(id, source: true)).GetProperty("items")[0].GetProperty("status").GetString().Should().Be("unavailable");
        f.Source = f.Source with { InvalidatedAtUtc = null };
        f.Conversation = f.Conversation with { UserId = "foreign" };
        Json(await f.Expand(id, source: true)).GetProperty("items")[0].GetProperty("status").GetString().Should().Be("unavailable");
        f.Conversation = f.Conversation with { UserId = "owner" };
        Json(await f.Expand(id, source: true)).GetProperty("items")[0].GetProperty("text").GetString().Should().Be("private-source-canary");
    }

    [Fact]
    public async Task Off_keeps_existing_tools_and_recall_and_rejects_evidence_operations()
    {
        await using var f = new Fixture(false);
        (await f.Call("recall_preview", new { session = "s", query = "q" })).StatusCode.Should().Be(404);
        (await f.Call("recall_expand", new { })).StatusCode.Should().Be(404);
        Json(await f.Call("recall", new { session = "s", query = "q" })).TryGetProperty("previewId", out _).Should().BeFalse();
        CodingToolDefinitions.For(false).Should().BeSameAs(CodingToolDefinitions.All);
    }

    [Fact]
    public async Task Null_preview_id_is_invalid_and_must_not_select_the_latest_preview()
    {
        await using var f = new Fixture(); await f.Preview();
        var result = await f.Call("recall_expand", new { session = "s", previewId = (string?)null,
            items = new[] { new { section = "relevantFacts", itemId = "f1" } } });
        result.StatusCode.Should().Be(400);
        f.History.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("owner", "alpha", "other")]
    [InlineData("owner", "beta", "s")]
    [InlineData("other", "alpha", "s")]
    public async Task Preview_is_not_authority_across_owner_project_or_session(string owner, string project, string session)
    {
        await using var f = new Fixture(); var p = await f.Preview();
        var result = await f.Call("recall_expand", new { session, previewId = p.GetProperty("previewId").GetString(),
            items = new[] { new { section = "relevantFacts", itemId = "f1" } } }, new(owner, project));
        result.StatusCode.Should().Be(404);
        f.History.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("future")]
    [InlineData("expired")]
    [InlineData("superseded")]
    [InlineData("merged")]
    [InlineData("source-unlinked")]
    public async Task Current_parent_controls_source_visibility(string change)
    {
        await using var f = new Fixture(); var p = await f.Preview();
        f.Row = change switch
        {
            "owner" => f.Row with { OwnerId = "other" },
            "future" => f.Row with { ValidFromUtc = DateTimeOffset.UtcNow.AddHours(1) },
            "expired" => f.Row with { ValidUntilUtc = DateTimeOffset.UtcNow.AddHours(-1) },
            "superseded" => f.Row with { SupersededByIds = ["replacement"] },
            "merged" => f.Row with { MergedIntoId = "replacement" },
            _ => f.Row with { SourceMessageIds = [] },
        };
        Json(await f.Expand(p.GetProperty("previewId").GetString()!, source: true))
            .GetProperty("items")[0].GetProperty("status").GetString().Should().Be("unavailable");
        f.Messages.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Evicted_and_expired_previews_do_not_read_storage()
    {
        var clock = new TestClock();
        await using var f = new Fixture(options: new() { RetainedPreviews = 1, LifetimeSeconds = 1 }, clock: clock);
        var first = (await f.Preview()).GetProperty("previewId").GetString()!;
        var second = (await f.Preview()).GetProperty("previewId").GetString()!;
        (await f.Expand(first)).StatusCode.Should().Be(404);
        clock.Now = clock.Now.AddSeconds(2);
        (await f.Expand(second)).StatusCode.Should().Be(404);
        f.History.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Expiration_during_a_read_discards_the_body()
    {
        var clock = new TestClock();
        await using var f = new Fixture(options: new() { LifetimeSeconds = 1 }, clock: clock);
        var id = (await f.Preview()).GetProperty("previewId").GetString()!;
        f.History.GetHistoryAsync(Arg.Any<MemoryHistoryQuery>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            clock.Now = clock.Now.AddSeconds(2);
            return Task.FromResult<IReadOnlyList<MemoryHistoryRecord>>([f.Row]);
        });
        (await f.Expand(id)).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Escaped_unicode_evidence_obeys_the_serialized_body_budget()
    {
        await using var f = new Fixture(options: new() { MaxBytes = 1024 });
        var id = (await f.Preview()).GetProperty("previewId").GetString()!;
        f.Row = f.Row with { Summary = string.Concat(Enumerable.Repeat("\U0001F600\n\"<&", 1000)) };
        var result = await f.Expand(id);
        result.StatusCode.Should().Be(200);
        JsonSerializer.SerializeToUtf8Bytes(result.Body, CodingApi.Json).Length.Should().BeLessThanOrEqualTo(1024);
        var json = Json(result);
        json.GetProperty("truncated").GetBoolean().Should().BeTrue();
        var item = json.GetProperty("items")[0];
        item.GetProperty("truncated").GetBoolean().Should().BeTrue();
        item.GetProperty("text").GetString().Should().NotBeNullOrEmpty().And.NotContain("\uFFFD");
        item.GetProperty("trust").GetString().Should().Be("untrusted_memory");
    }

    [Fact]
    public async Task Preview_budget_omissions_cannot_be_expanded_and_do_not_change_retrieval_options()
    {
        await using var f = new Fixture(options: new() { MaxBytes = 1024, MaxItems = 1 });
        var fact = new Fact { FactId = "omitted", Subject = "S", Predicate = "P", Object = "O", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch };
        f.Result = f.Result with { Context = f.Result.Context with { RelevantFacts = new() { Items = [f.Result.Context.RelevantFacts.Items[0], fact] } } };
        var p = await f.Preview();
        p.GetProperty("truncated").GetBoolean().Should().BeTrue();
        p.GetProperty("items").GetArrayLength().Should().Be(1);
        JsonSerializer.SerializeToUtf8Bytes(p, CodingApi.Json).Length.Should().BeLessThanOrEqualTo(1024);
        Json(await f.Expand(p.GetProperty("previewId").GetString()!, id: "omitted"))
            .GetProperty("items")[0].GetProperty("status").GetString().Should().Be("unavailable");
        f.History.ReceivedCalls().Should().BeEmpty();
        await f.Recall.Received(1).RecallAsync(Arg.Is<RecallRequest>(r => !r.Options.IncludeDiagnostics), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_assertions_are_not_verified_and_do_not_expose_arbitrary_metadata(bool persistedJson)
    {
        await using var f = new Fixture(); var id = (await f.Preview()).GetProperty("previewId").GetString()!;
        f.Source = f.Source with { Metadata = new Dictionary<string, object> { ["coding_commit"] = new string('a', 40),
            ["coding_source"] = "src/example.cs", ["private-other"] = "private-metadata-canary" } };
        if (persistedJson) f.Source = f.Source with { Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(f.Source.Metadata))! };
        var result = await f.Expand(id, source: true);
        var assertion = Json(result).GetProperty("items")[0].GetProperty("assertion");
        assertion.GetProperty("verification").GetString().Should().Be("caller_asserted");
        assertion.GetProperty("source").GetString().Should().Be("src/example.cs");
        JsonSerializer.Serialize(result.Body).Should().NotContain("private-metadata-canary");
    }

    [Fact]
    public async Task Evidence_only_is_independent_of_diagnostics_and_preserves_ordinary_recall_bytes()
    {
        await using var baseline = new Fixture(false); await using var enabled = new Fixture();
        baseline.App.Services.GetService<CodingEvidence>().Should().BeNull();
        baseline.App.Services.GetService<CodingEvidenceReader>().Should().BeNull();
        enabled.App.Services.GetService<CodingRecallReceipts>().Should().BeNull();
        var a = Json(await baseline.Call("recall", new { session = "s", query = "q" }));
        var b = Json(await enabled.Call("recall", new { session = "s", query = "q" }));
        a.GetRawText().Should().Be(b.GetRawText());
        (await enabled.Call("recall_status", new { session = "s" })).StatusCode.Should().Be(404);
        var p = await enabled.Preview();
        (await enabled.Call("recall_expand", new { session = "s", previewId = p.GetProperty("previewId").GetString(),
            items = new[] { new { section = "relevantFacts", itemId = "f1" } } }, Owner with { CanWrite = false })).StatusCode.Should().Be(200);
    }

    [Theory]
    [InlineData("asOf")]
    [InlineData("path")]
    [InlineData("ownerId")]
    public async Task Undeclared_expansion_fields_are_rejected(string field)
    {
        await using var f = new Fixture(); var id = (await f.Preview()).GetProperty("previewId").GetString()!;
        var args = new Dictionary<string, object> { ["session"] = "s", ["previewId"] = id,
            ["items"] = new[] { new { section = "relevantFacts", itemId = "f1" } }, [field] = "untrusted" };
        (await f.Call("recall_expand", args)).StatusCode.Should().Be(400);
        f.History.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_returns_a_bounded_error_without_partial_evidence(bool expansion)
    {
        await using var f = new Fixture(options: new() { DeadlineMilliseconds = 100 });
        var id = (await f.Preview()).GetProperty("previewId").GetString()!;
        if (expansion)
            f.History.GetHistoryAsync(Arg.Any<MemoryHistoryQuery>(), Arg.Any<CancellationToken>()).Returns(async call =>
            { await Task.Delay(10000, call.Arg<CancellationToken>()); return (IReadOnlyList<MemoryHistoryRecord>)[f.Row]; });
        else
            f.Recall.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
            { await Task.Delay(10000, call.Arg<CancellationToken>()); return f.Result; });
        var result = expansion ? await f.Expand(id) : await f.Call("recall_preview", new { session = "s", query = "q" });
        result.StatusCode.Should().Be(504);
        Json(result).GetProperty("error").GetString().Should().Be("evidence_timeout");
        JsonSerializer.Serialize(result.Body).Should().NotContain("private-");
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        await using var f = new Fixture(); using var cts = new CancellationTokenSource(); cts.Cancel();
        var call = () => f.App.Services.GetRequiredService<CodingApi>().ExecuteAsync(Owner, "recall_preview",
            JsonSerializer.SerializeToElement(new { session = "s", query = "q" }), cts.Token);
        await call.Should().ThrowAsync<OperationCanceledException>();
        f.Recall.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Direct_messages_and_traces_are_reread_and_owner_checked()
    {
        await using var f = new Fixture();
        f.Result = f.Result with { Context = f.Result.Context with
        { RecentMessages = new() { Items = [f.Source] }, SimilarTraces = new() { Items = [f.Trace!] } } };
        var id = (await f.Preview()).GetProperty("previewId").GetString()!;
        var items = new[] { new { section = "recentMessages", itemId = "m1" }, new { section = "similarTraces", itemId = "t1" } };
        async Task<JsonElement> Read() => Json(await f.Call("recall_expand", new { session = "s", previewId = id, items }));
        var available = (await Read()).GetProperty("items");
        available[0].GetProperty("text").GetString().Should().Be("private-source-canary");
        available[1].GetProperty("text").GetString().Should().Contain("Reported outcome: reported-pass");
        f.Conversation = f.Conversation with { SessionId = "different" };
        f.Trace = f.Trace! with { OwnerId = "other" };
        (await Read()).GetProperty("items").EnumerateArray().Should().OnlyContain(x => x.GetProperty("status").GetString() == "unavailable");
        f.Trace = null;
        (await Read()).GetProperty("items")[1].GetProperty("status").GetString().Should().Be("unavailable");
    }

    [Theory]
    [InlineData("relevantEntities", MemoryHistoryKind.Entity)]
    [InlineData("relevantPreferences", MemoryHistoryKind.Preference)]
    public async Task Entity_and_preference_expansion_reuses_live_history_guard(string section, MemoryHistoryKind kind)
    {
        await using var f = new Fixture();
        var reader = f.App.Services.GetRequiredService<CodingEvidenceReader>();
        f.Row = f.Row with { Kind = kind };
        var request = new CodingEvidenceSelection { Section = section, ItemId = "f1" };
        var selected = new CodingRecallItem(section, "f1", ["m1"]);
        (await reader.ReadAsync(Owner, request, selected, default)).Status.Should().Be("available");
        f.Row = f.Row with { InvalidatedAtUtc = DateTimeOffset.UtcNow };
        (await reader.ReadAsync(Owner, request, selected, default)).Status.Should().Be("unavailable");
        await f.History.Received(2).GetHistoryAsync(Arg.Is<MemoryHistoryQuery>(q => q.Kind == kind && q.OwnerId == "owner" && !q.IncludeShared && !q.IncludeInvalidated), Arg.Any<CancellationToken>());
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static JsonElement Json(CodingApiResult result) => JsonSerializer.SerializeToElement(result.Body, CodingApi.Json);
    private sealed class Fixture : IAsyncDisposable
    {
        public IReasoningTraceRepository Traces { get; } = Substitute.For<IReasoningTraceRepository>();
        public ReasoningTrace? Trace { get; set; } = new() { TraceId = "t1", SessionId = "old", OwnerId = "owner",
            Task = "reported-task", Outcome = "reported-pass", StartedAtUtc = DateTimeOffset.UnixEpoch };
        public IMemoryRecall Recall { get; } = Substitute.For<IMemoryRecall>();
        public IMemoryHistoryService History { get; } = Substitute.For<IMemoryHistoryService>();
        public IMessageRepository Messages { get; } = Substitute.For<IMessageRepository>();
        public IConversationRepository Conversations { get; } = Substitute.For<IConversationRepository>();
        public MemoryHistoryRecord Row { get; set; } = new() { Kind = MemoryHistoryKind.Fact, Id = "f1", OwnerId = "owner",
            Status = MemoryHistoryStatus.Live, CreatedAtUtc = DateTimeOffset.UnixEpoch, Summary = "private-fact-canary", SourceMessageIds = ["m1"] };
        public Message Source { get; set; } = new() { MessageId = "m1", ConversationId = "c1", SessionId = "stored-s",
            Role = "user", Content = "private-source-canary", TimestampUtc = DateTimeOffset.UnixEpoch };
        public Conversation Conversation { get; set; } = new() { ConversationId = "c1", SessionId = "stored-s", UserId = "owner",
            CreatedAtUtc = DateTimeOffset.UnixEpoch, UpdatedAtUtc = DateTimeOffset.UnixEpoch };
        public WebApplication App { get; }
        public RecallResult Result { get; set; } = null!;
        public Fixture(bool enabled = true, CodingEvidenceOptions? options = null, TimeProvider? clock = null)
        {
            var json = enabled ? "{\"evidence\":" + JsonSerializer.Serialize(options ?? new(), CodingApi.Json) + "," + CodingConfigurationTests.Json[1..] : CodingConfigurationTests.Json;
            var config = CodingConfiguration.Parse(json, CodingConfigurationTests.EnvironmentValues().GetValueOrDefault);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            CodingHost.Configure(builder, config);
            Result = new RecallResult
            {
                Context = new MemoryContext { SessionId = "s", AssembledAtUtc = DateTimeOffset.UnixEpoch,
                    RelevantFacts = new() { Items = [new Fact { FactId = "f1", Subject = "Project", Predicate = "uses", Object = "private-fact-canary",
                        Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch, OwnerId = "owner", SourceMessageIds = ["m1"] }] } }, TotalItemsRetrieved = 1,
            };
            Recall.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Result));
            if (clock is not null) builder.Services.AddSingleton(clock);
            History.GetHistoryAsync(Arg.Any<MemoryHistoryQuery>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IReadOnlyList<MemoryHistoryRecord>>([Row]));
            Messages.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Message?>(Source));
            Conversations.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Conversation?>(Conversation));
            Traces.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<ReasoningTrace?>>(_ => Task.FromResult<ReasoningTrace?>(Trace));
            builder.Services.RemoveAll<IReasoningTraceRepository>(); builder.Services.AddSingleton(Traces);
            builder.Services.RemoveAll<IMemoryRecall>(); builder.Services.AddSingleton(Recall);
            builder.Services.RemoveAll<IMemoryHistoryService>(); builder.Services.AddSingleton(History);
            builder.Services.RemoveAll<IMessageRepository>(); builder.Services.AddSingleton(Messages);
            builder.Services.RemoveAll<IConversationRepository>(); builder.Services.AddSingleton(Conversations);
            App = builder.Build();
        }
        public Task<CodingApiResult> Call(string operation, object args, CodingIdentity? identity = null) =>
            App.Services.GetRequiredService<CodingApi>().ExecuteAsync(identity ?? Owner, operation, JsonSerializer.SerializeToElement(args));
        public async Task<JsonElement> Preview() { var r = await Call("recall_preview", new { session = "s", query = "q" }); r.StatusCode.Should().Be(200); return Json(r); }
        public Task<CodingApiResult> Expand(string preview, string id = "f1", bool source = false) => Call("recall_expand",
            new { session = "s", previewId = preview, items = new[] { new { section = "relevantFacts", itemId = id, sourceMessageId = source ? "m1" : null } } });
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}
