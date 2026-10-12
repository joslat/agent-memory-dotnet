using AgentMemory.Abstractions.Domain;
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

public sealed class CodingApiTests
{
    private static readonly CodingIdentity Identity = new("owner", "alpha");

    [Theory]
    [InlineData("recall", "{\"session\":\"s\",\"query\":\"q\",\"ownerId\":\"other\"}")]
    [InlineData("recall", "{\"query\":\"q\"}")]
    [InlineData("forget", "{\"kind\":0,\"id\":\"id\"}")]
    [InlineData("profile", "{\"project\":\"beta\"}")]
    [InlineData("history", "null")]
    public async Task Invalid_or_scope_overriding_arguments_are_rejected_before_memory_access(string operation, string json)
    {
        await using var fixture = new Fixture();
        var result = await fixture.Api.ExecuteAsync(Identity, operation, JsonSerializer.Deserialize<JsonElement>(json));
        result.StatusCode.Should().Be(400);
        fixture.Recall.ReceivedCalls().Should().BeEmpty();
        fixture.Profile.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Recall_uses_the_bound_scope_and_removes_vectors_from_the_response()
    {
        await using var fixture = new Fixture();
        var result = await fixture.Api.ExecuteAsync(Identity, "recall", JsonSerializer.SerializeToElement(new { session = "s", query = "q" }));
        result.StatusCode.Should().Be(200);
        var json = JsonSerializer.Serialize(result.Body);
        json.Should().NotContain("Embedding").And.NotContain("embedding");
        json.Should().Contain("remembered-value");
        await fixture.Recall.Received(1).RecallAsync(Arg.Is<RecallRequest>(r => r.UserId == "owner" &&
            r.Options.Scope == MemoryScope.For("owner", false)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Profile_has_no_caller_selectable_owner()
    {
        await using var fixture = new Fixture();
        var result = await fixture.Api.ExecuteAsync(Identity, "profile", JsonSerializer.SerializeToElement(new { }));
        result.StatusCode.Should().Be(200);
        await fixture.Profile.Received(1).GetAsync("owner", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remember_reports_acceptance_and_status_is_project_scoped()
    {
        await using var fixture = new Fixture();
        var request = JsonSerializer.SerializeToElement(new
        {
            session = "s", turnId = "one", userText = "Use port 5234.", commit = new string('a', 40), source = "settings.cs",
        });
        var result = await fixture.Api.ExecuteAsync(Identity, "remember", request);
        result.StatusCode.Should().Be(202);
        var job = result.Body.Should().BeOfType<CodingWriteJob>().Subject;
        job.State.Should().Be(CodingJobState.Queued);
        var args = JsonSerializer.SerializeToElement(new { jobId = job.JobId });
        (await fixture.Api.ExecuteAsync(Identity, "write_status", args)).StatusCode.Should().Be(200);
        (await fixture.Api.ExecuteAsync(Identity with { ProjectId = "beta" }, "write_status", args)).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Provider_exceptions_are_not_returned_to_the_client()
    {
        await using var fixture = new Fixture();
        fixture.Profile.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkingMemoryBlock?>(new InvalidOperationException("private canary content")));
        var result = await fixture.Api.ExecuteAsync(Identity, "profile", JsonSerializer.SerializeToElement(new { }));
        result.StatusCode.Should().Be(503);
        JsonSerializer.Serialize(result.Body).Should().NotContain("canary");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public IMemoryRecall Recall { get; } = Substitute.For<IMemoryRecall>();
        public IWorkingMemoryService Profile { get; } = Substitute.For<IWorkingMemoryService>();
        public CodingApi Api { get; }
        private readonly ServiceProvider _provider;
        private readonly CodingWriteQueue _queue;

        public Fixture()
        {
            Recall.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(new RecallResult
            {
                Context = new MemoryContext
                {
                    SessionId = "s", AssembledAtUtc = DateTimeOffset.UtcNow,
                    RelevantFacts = new MemoryContextSection<Fact>
                    {
                        Items = [new Fact { FactId = "fact", Subject = "Project", Predicate = "uses", Object = "remembered-value",
                            Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch, Embedding = [0.125f, 0.75f] }],
                    },
                },
                TotalItemsRetrieved = 1, Truncated = false,
            });
            _provider = new ServiceCollection().AddSingleton(Recall).AddSingleton(Profile)
                .AddSingleton<IWritableMemoryOwnerContext, DefaultMemoryOwnerContext>()
                .AddSingleton<IWritableMemoryStoreContext, DefaultMemoryStoreContext>()
                .AddSingleton(Options.Create(new MemoryStoreOptions
                {
                    Strategy = MemoryStorageStrategy.DatabasePerApplication, DatabasePrefix = "coding-", AutoProvision = false,
                })).BuildServiceProvider();
            var content = new CodingContentPolicy([]);
            var scope = new CodingOperationScope(_provider.GetRequiredService<IServiceScopeFactory>());
            _queue = new CodingWriteQueue(new CodingWriter(scope, content), content);
            Api = new CodingApi(new CodingMemoryOperations(scope, Options.Create(new MemoryOptions()), content), _queue);
        }

        public async ValueTask DisposeAsync() { _queue.Dispose(); await _provider.DisposeAsync(); }
    }
}
