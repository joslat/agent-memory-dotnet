using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.RouterArena.Record;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AgentMemory.Tests.Unit.RouterArena;

public sealed class StoreSearchCaptureTests
{
    [Fact]
    public async Task Capture_uses_the_library_wide_pool_and_preserves_item_mapping_and_ungated_context()
    {
        var now = DateTimeOffset.UnixEpoch;
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(now);
        var inner = Substitute.For<IMemoryContextAssembler>();
        var context = new MemoryContext
        {
            SessionId = "session", AssembledAtUtc = now, Truncated = true, WorkingMemoryBlock = "Actual profile",
            RelevantFacts = new() { Items = [new Fact { FactId = "stored-fact", Subject = "Task", Predicate = "status", Object = "planned", CreatedAtUtc = now, Confidence = 1 }] },
            RecentMessages = new() { Items = [new Message { MessageId = "message", SessionId = "session", ConversationId = "conversation",
                Role = "user", Content = "Earlier turn", TimestampUtc = now }] },
        };
        inner.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(context);
        var capture = new StoreSearchCapture(inner, new MemoryOptions(), clock);
        var result = await capture.CaptureAsync(new SearchCaptureQuery { Id = "case", Session = 1, Question = "What changed?" },
            "session", "owner", 50);

        var request = inner.ReceivedCalls().Single().GetArguments()[0].Should().BeOfType<RecallRequest>().Subject;
        request.Options.MinSimilarityScore.Should().Be(0);
        request.Options.MaxFacts.Should().BeGreaterThanOrEqualTo(50);
        request.Options.IncludeDiagnostics.Should().BeTrue();
        request.Options.Scope.Should().Be(MemoryScope.For("owner", includeShared: false));
        result.Context!.WorkingMemoryBlock.Should().Be("Actual profile");
        result.Context.Truncated.Should().BeTrue();
        result.Entries.Single().ItemId.Should().Be("stored-fact");
        result.Request!.Candidates.Single().Key.Should().Be(result.Entries.Single().Candidate.Key);
        result.Request.Conversation.Single().Text.Should().Be("Earlier turn");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task Historical_capture_uses_the_actual_as_of_path_and_has_no_gate_request()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);
        var inner = Substitute.For<IMemoryContextAssembler>();
        inner.AssembleContextAsOfAsync(Arg.Any<RecallRequest>(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Arg.Any<CancellationToken>())
            .Returns(new MemoryContext { SessionId = "session", AssembledAtUtc = DateTimeOffset.UnixEpoch });
        var result = await new StoreSearchCapture(inner, new MemoryOptions(), clock).CaptureAsync(
            new SearchCaptureQuery { Id = "historical", Session = 1, Question = "What was known then?", Historical = true }, "session", "owner", 50);
        await inner.DidNotReceiveWithAnyArgs().AssembleContextAsync(default!, default);
        result.Request.Should().BeNull();
        result.AsOfUtc.Should().Be(DateTimeOffset.UnixEpoch);
        result.Context.Should().NotBeNull();
    }

    [Fact]
    public async Task Failed_search_stays_failed_and_does_not_become_an_empty_pool()
    {
        var clock = Substitute.For<IClock>();
        var inner = Substitute.For<IMemoryContextAssembler>();
        inner.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<MemoryContext>>(_ => throw new InvalidOperationException("private detail"));
        var result = await new StoreSearchCapture(inner, new MemoryOptions(), clock).CaptureAsync(
            new SearchCaptureQuery { Id = "case", Session = 1, Question = "Question" }, "session", "owner", 50);
        result.Error.Should().Be("InvalidOperationException");
        result.Context.Should().BeNull();
        result.Request.Should().BeNull();
    }

    [Fact]
    public void Registration_retains_the_original_assembler_and_captures_its_factory_before_gate_decoration()
    {
        var services = new ServiceCollection();
        var inner = Substitute.For<IMemoryContextAssembler>();
        var clock = Substitute.For<IClock>();
        services.AddSingleton(clock);
        services.AddScoped<IMemoryContextAssembler>(_ => inner);
        StoreSearchCapture.Register(services, new MemoryOptions());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMemoryContextAssembler>().Should().BeSameAs(inner);
        scope.ServiceProvider.GetRequiredService<StoreSearchCapture>().Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Partial_search_retains_diagnostics_but_cannot_be_used_as_a_successful_gate_pool(bool globalTimeout)
    {
        var inner = Substitute.For<IMemoryContextAssembler>();
        inner.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(new MemoryContext
        {
            SessionId = "session", AssembledAtUtc = DateTimeOffset.UnixEpoch,
            LatencyBudgetExceeded = globalTimeout,
            RelevantFacts = new() { Diagnostics = new(true, 50, 0, null, null, 0, TimedOut: !globalTimeout) },
        });
        var result = await new StoreSearchCapture(inner, new MemoryOptions(), Substitute.For<IClock>()).CaptureAsync(
            new SearchCaptureQuery { Id = "case", Session = 1, Question = "Question" }, "session", "owner", 50);

        result.Error.Should().Be("PartialSearchLatencyBudgetExceeded");
        result.Context.Should().NotBeNull("partial evidence must survive for diagnosis");
        result.Request.Should().BeNull("partial retrieval must not silently enter downstream judging");
    }

    [Fact]
    public async Task Search_capture_requires_provenance_before_reading_any_world_or_resolving_providers()
    {
        var code = await new StoreSessions(TextWriter.Null).RunAsync("missing-pack", "missing-sets", "writer", "missing-output", true, null,
            searchCasesPath: "missing-queries");
        code.Should().Be(1);
    }

    [Theory]
    [InlineData("holdout", "1", "")]
    [InlineData("development", "2", "")]
    [InlineData("development", "1", ",\"gold\":\"answer\"")]
    [InlineData("development", "1", ",\"question\":\"duplicate\"")]
    public async Task Search_plan_refuses_holdouts_unselected_sessions_and_label_fields(string role, string session, string extra)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, $$"""
                {"schemaVersion":1,"role":"{{role}}","wideLimit":50,"cases":[{"id":"case","session":{{session}},"question":"Question"{{extra}}}]}
                """);
            var act = () => SearchCapturePlan.ReadAsync(path, [new StoreSessions.Turn("s01:01", 1, "2026-01-01", "Turn", [])], default);
            await act.Should().ThrowAsync<Exception>();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Search_plan_freezes_its_input_file_and_detects_later_changes()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """{"schemaVersion":1,"role":"synthetic-development","wideLimit":50,"cases":[{"id":"case","session":1,"question":"Question"}]}""");
            var plan = await SearchCapturePlan.ReadAsync(path, [new StoreSessions.Turn("s01:01", 1, "2026-01-01", "Turn", [])], default);
            plan.VerifyInput();
            plan.Cases.Single().Question.Should().Be("Question");
            await File.WriteAllTextAsync(path, "{}");
            var verify = () => plan.VerifyInput();
            verify.Should().Throw<InvalidOperationException>();
        }
        finally { File.Delete(path); }
    }
}
