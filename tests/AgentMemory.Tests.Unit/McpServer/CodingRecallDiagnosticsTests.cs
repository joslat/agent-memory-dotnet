using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpHost.Coding;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingRecallDiagnosticsTests
{
    private static readonly CodingIdentity Owner = new("owner", "alpha");

    [Fact]
    public async Task Enabled_recall_produces_a_scoped_receipt_without_retaining_content()
    {
        await using var f = new Fixture(true);
        var recalled = await f.Call("recall", new { session = "s", query = "private-query-canary" });
        recalled.StatusCode.Should().Be(200);
        var id = Json(recalled).GetProperty("recallId").GetString();
        var receipt = await f.Call("recall_status", new { session = "s", recallId = id });
        receipt.StatusCode.Should().Be(200);
        var text = JsonSerializer.Serialize(receipt.Body, CodingApi.Json);
        text.Should().Contain("fact-one").And.NotContain("private-query-canary").And.NotContain("private-memory-canary");
        (await f.Call("recall_status", new { session = "other", recallId = id })).StatusCode.Should().Be(404);
        (await f.Call("recall_status", new { session = "s", recallId = id }, Owner with { ProjectId = "beta" })).StatusCode.Should().Be(404);
        (await f.Call("recall_status", new { session = "s", recallId = id }, Owner with { OwnerId = "other" })).StatusCode.Should().Be(404);
        await f.Recall.Received(1).RecallAsync(Arg.Is<RecallRequest>(r => r.Options.IncludeDiagnostics), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Off_keeps_legacy_recall_and_has_no_receipt_operations()
    {
        await using var f = new Fixture(false);
        var recalled = await f.Call("recall", new { session = "s", query = "q" });
        Json(recalled).TryGetProperty("recallId", out _).Should().BeFalse();
        (await f.Call("recall_status", new { session = "s" })).StatusCode.Should().Be(404);
        (await f.Call("recall_feedback", new { session = "s", recallId = "missing", label = "useful" })).StatusCode.Should().Be(404);
        await f.Recall.Received(1).RecallAsync(Arg.Is<RecallRequest>(r => !r.Options.IncludeDiagnostics), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Feedback_uses_existing_write_permission_and_strict_labels()
    {
        await using var f = new Fixture(true);
        var recalled = await f.Call("recall", new { session = "s", query = "q" });
        var id = Json(recalled).GetProperty("recallId").GetString();
        var args = new { session = "s", recallId = id, label = "useful" };
        (await f.Call("recall_feedback", args, Owner with { CanWrite = false })).StatusCode.Should().Be(403);
        (await f.Call("recall_feedback", args)).StatusCode.Should().Be(200);
        Json(await f.Call("recall_status", new { session = "s" })).GetProperty("feedback").GetString().Should().Be("useful");
        (await f.Call("recall_feedback", new { session = "s", recallId = id, label = "auto-train" })).StatusCode.Should().Be(400);
        (await f.Call("recall_feedback", new { session = "s", recallId = id, label = "useful", text = "private" })).StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Failed_and_cancelled_recalls_leave_no_receipt()
    {
        await using var f = new Fixture(true);
        f.Recall.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<RecallResult>(new InvalidOperationException("private-provider-canary")));
        var failure = await f.Call("recall", new { session = "s", query = "q" });
        failure.StatusCode.Should().Be(503);
        JsonSerializer.Serialize(failure.Body).Should().NotContain("private");
        (await f.Call("recall_status", new { session = "s" })).StatusCode.Should().Be(404);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var cancelled = () => f.App.Services.GetRequiredService<CodingApi>().ExecuteAsync(Owner, "recall",
            JsonSerializer.SerializeToElement(new { session = "s", query = "q" }), cts.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await f.Call("recall_status", new { session = "s" })).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Off_registers_no_receipt_service_and_keeps_context_bytes_when_on()
    {
        await using var baseline = new Fixture(false); await using var enabled = new Fixture(true);
        baseline.App.Services.GetService<CodingRecallReceipts>().Should().BeNull();
        var a = Json(await baseline.Call("recall", new { session = "s", query = "q" }));
        var b = Json(await enabled.Call("recall", new { session = "s", query = "q" }));
        a.GetProperty("context").GetRawText().Should().Be(b.GetProperty("context").GetRawText());
        a.EnumerateObject().Select(x => x.Name).Should().Equal("totalItemsRetrieved", "truncated", "estimatedTokenCount", "context");
        CodingToolDefinitions.For(false).Should().BeSameAs(CodingToolDefinitions.All);
        CodingToolDefinitions.For(false, true).Select(x => x.Name).Should().EndWith(new[] { "recall_status", "recall_feedback" });
    }

    private static JsonElement Json(CodingApiResult result) => JsonSerializer.SerializeToElement(result.Body, CodingApi.Json);

    private sealed class Fixture : IAsyncDisposable
    {
        public IMemoryRecall Recall { get; } = Substitute.For<IMemoryRecall>();
        public WebApplication App { get; }
        public Fixture(bool enabled)
        {
            var configJson = enabled ? "{\"recallDiagnostics\":{}," + CodingConfigurationTests.Json[1..] : CodingConfigurationTests.Json;
            var config = CodingConfiguration.Parse(configJson, CodingConfigurationTests.EnvironmentValues().GetValueOrDefault);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            CodingHost.Configure(builder, config);
            Recall.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(new RecallResult
            {
                Context = new MemoryContext
                {
                    SessionId = "s", AssembledAtUtc = DateTimeOffset.UnixEpoch,
                    RelevantFacts = new MemoryContextSection<Fact>
                    {
                        Items = [new Fact { FactId = "fact-one", Subject = "Project", Predicate = "uses", Object = "private-memory-canary",
                            Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch }],
                    },
                }, TotalItemsRetrieved = 1,
            });
            builder.Services.RemoveAll<IMemoryRecall>();
            builder.Services.AddSingleton(Recall);
            App = builder.Build();
        }
        public Task<CodingApiResult> Call(string operation, object args, CodingIdentity? identity = null) =>
            App.Services.GetRequiredService<CodingApi>().ExecuteAsync(identity ?? Owner, operation, JsonSerializer.SerializeToElement(args));
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}
