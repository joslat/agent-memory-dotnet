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

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingMemoryOperationsTests
{
    private static readonly CodingIdentity Identity = new("owner", "alpha");

    [Fact]
    public async Task Secret_query_is_rejected_before_recall_or_embedding()
    {
        var recall = Substitute.For<IMemoryRecall>();
        await using var provider = Services().AddSingleton(recall).BuildServiceProvider();
        var run = () => Operations(provider).RecallAsync(Identity, "session", "canary-credential-value");
        await run.Should().ThrowAsync<ArgumentException>();
        recall.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Recall_binds_owner_and_excludes_shared_records_with_a_scoped_session()
    {
        var recall = Substitute.For<IMemoryRecall>();
        var options = new MemoryOptions { Recall = RecallOptions.Default with { MaxFacts = 7 } };
        await using var provider = Services().AddSingleton(recall).BuildServiceProvider();
        var operations = Operations(provider, options);
        using var stop = new CancellationTokenSource();
        await operations.RecallAsync(Identity, "client-session", "query", stop.Token);
        await recall.Received(1).RecallAsync(Arg.Is<RecallRequest>(r =>
            r.UserId == "owner" && r.Query == "query" && r.SessionId != "client-session" &&
            r.Options.Scope == MemoryScope.For("owner", false) && r.Options.MaxFacts == 7), stop.Token);
    }

    [Fact]
    public async Task Same_client_session_is_distinct_across_owners_and_projects()
    {
        var recall = Substitute.For<IMemoryRecall>();
        var captured = new List<RecallRequest>();
        _ = recall.RecallAsync(Arg.Do<RecallRequest>(captured.Add), Arg.Any<CancellationToken>());
        await using var provider = Services().AddSingleton(recall).BuildServiceProvider();
        var operations = Operations(provider);
        await operations.RecallAsync(Identity, "session", "q");
        await operations.RecallAsync(Identity, "session", "q");
        await operations.RecallAsync(Identity with { OwnerId = "other" }, "session", "q");
        await operations.RecallAsync(Identity with { ProjectId = "beta" }, "session", "q");
        captured.Should().HaveCount(4);
        captured[0].SessionId.Should().Be(captured[1].SessionId);
        captured.Select(r => r.SessionId).Distinct().Should().HaveCount(3);
    }

    [Theory]
    [InlineData(MemoryHistoryKind.Fact)]
    [InlineData(MemoryHistoryKind.Entity)]
    [InlineData(MemoryHistoryKind.Preference)]
    public async Task Forget_only_calls_the_scoped_non_destructive_operation(MemoryHistoryKind kind)
    {
        var longTerm = Substitute.For<ILongTermMemoryService>();
        await using var provider = Services().AddSingleton(longTerm).BuildServiceProvider();
        using var stop = new CancellationTokenSource();
        await Operations(provider).ForgetAsync(Identity, kind, "memory-id", stop.Token);
        var scope = MemoryScope.For("owner", false);
        switch (kind)
        {
            case MemoryHistoryKind.Fact: await longTerm.Received(1).InvalidateFactAsync("memory-id", scope, stop.Token); break;
            case MemoryHistoryKind.Entity: await longTerm.Received(1).InvalidateEntityAsync("memory-id", scope, stop.Token); break;
            case MemoryHistoryKind.Preference: await longTerm.Received(1).InvalidatePreferenceAsync("memory-id", scope, stop.Token); break;
        }
        longTerm.ReceivedCalls().Should().ContainSingle();
    }

    [Fact]
    public async Task History_includes_closed_records_only_for_the_bound_owner()
    {
        var history = Substitute.For<IMemoryHistoryService>();
        await using var provider = Services().AddSingleton(history).BuildServiceProvider();
        using var stop = new CancellationTokenSource();
        await Operations(provider).HistoryAsync(Identity, "memory-id", stop.Token);
        await history.Received(1).GetHistoryAsync(Arg.Is<MemoryHistoryQuery>(q => q.OwnerId == "owner" &&
            !q.IncludeShared && q.IncludeInvalidated && q.Id == "memory-id" && q.Limit == 50), stop.Token);
    }

    [Fact]
    public async Task Profile_reads_the_bound_owner()
    {
        var profile = Substitute.For<IWorkingMemoryService>();
        await using var provider = Services().AddSingleton(profile).BuildServiceProvider();
        using var stop = new CancellationTokenSource();
        await Operations(provider).ProfileAsync(Identity, stop.Token);
        await profile.Received(1).GetAsync("owner", stop.Token);
    }

    [Fact]
    public async Task Invalid_inputs_are_rejected_without_resolving_a_memory_service()
    {
        // No memory services are registered: invalid inputs must stop before trying to resolve one.
        await using var provider = Services().BuildServiceProvider();
        var operations = Operations(provider);
        var badQuery = () => operations.RecallAsync(Identity, "session", new string('x', 8001));
        var badSession = () => operations.RecallAsync(Identity, "", "query");
        var badKind = () => operations.ForgetAsync(Identity, (MemoryHistoryKind)100, "id");
        var badId = () => operations.ForgetAsync(Identity, MemoryHistoryKind.Fact, "");
        var badHistory = () => operations.HistoryAsync(Identity, new string('x', 257));
        await badQuery.Should().ThrowAsync<ArgumentException>();
        await badSession.Should().ThrowAsync<ArgumentException>();
        await badKind.Should().ThrowAsync<ArgumentException>();
        await badId.Should().ThrowAsync<ArgumentException>();
        await badHistory.Should().ThrowAsync<ArgumentException>();
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWritableMemoryOwnerContext, DefaultMemoryOwnerContext>();
        services.AddSingleton<IWritableMemoryStoreContext, DefaultMemoryStoreContext>();
        services.AddSingleton(Options.Create(new MemoryStoreOptions
        {
            Strategy = MemoryStorageStrategy.DatabasePerApplication, DatabasePrefix = "coding-", AutoProvision = false,
        }));
        return services;
    }

    private static CodingMemoryOperations Operations(ServiceProvider provider, MemoryOptions? options = null) =>
        new(new CodingOperationScope(provider.GetRequiredService<IServiceScopeFactory>()), Options.Create(options ?? new()), new(["canary-credential-value"]));
}
