using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Domain;
using AgentMemory.Nams.Observability;
using AgentMemory.Nams.Recall;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsRecallServiceTests
{
    private sealed class FakeNamsClient : INamsClient
    {
        public Func<CancellationToken, Task<NamsContext>>? OnGetContext { get; set; }
        public Func<string, int, CancellationToken, Task<IReadOnlyList<NamsEntity>>>? OnSearchEntities { get; set; }
        public int SearchEntitiesCallCount { get; private set; }

        public Task<NamsConversation> CreateConversationAsync(
            string? userId, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsContext> GetContextAsync(string conversationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); // matches Neo4jNamsClientAdapter's real behavior
            return (OnGetContext ?? (_ => Task.FromResult(EmptyContext)))(cancellationToken);
        }

        public Task<IReadOnlyList<NamsMessage>> AddMessagesAsync(
            string conversationId, IReadOnlyList<NamsMessageInput> messages, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsEntity>> SearchEntitiesAsync(
            string query, string? type, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); // matches Neo4jNamsClientAdapter's real behavior
            SearchEntitiesCallCount++;
            return (OnSearchEntities ?? ((_, _, _) => Task.FromResult<IReadOnlyList<NamsEntity>>([])))(query, limit, cancellationToken);
        }

        public Task<IReadOnlyList<NamsMessage>> SearchMessagesAsync(
            string conversationId, string query, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsEntity>> ListEntitiesAsync(int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsConversationSummary>> ListConversationsAsync(int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsObservation>> GetObservationsAsync(
            string conversationId, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsEntityFeedbackResult> SetEntityFeedbackAsync(
            string entityId, double? userScore, bool? confirmed, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsEntityGraph> GetEntityGraphAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsGraphExpansion> ExpandGraphAsync(
            string nodeId, IReadOnlyList<string> loadedIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsReasoningStep> RecordReasoningStepAsync(
            string conversationId, string reasoning, string actionTaken, string? result, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsReasoningStep>> ListReasoningStepsAsync(string conversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsToolCall> RecordToolCallAsync(
            string? stepId, string toolName, string input, string? output, string? status, int? durationMs,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsReasoningTrace> GetReasoningTraceAsync(string conversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsEntityProvenance> GetEntityProvenanceAsync(string entityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static readonly NamsContext EmptyContext = new([], [], []);

    private static NamsRecallService CreateService(FakeNamsClient client, NamsRecallOptions? options = null) =>
        new(client, Options.Create(options ?? new NamsRecallOptions()), NullLogger<NamsRecallService>.Instance, new NamsMetrics());

    [Fact]
    public async Task RecallAsync_MapsReflectionsObservationsAndRecentMessages()
    {
        var client = new FakeNamsClient
        {
            OnGetContext = _ => Task.FromResult(new NamsContext(
                Reflections: [new NamsReflection("r1", "conv-1", "insight", "2026-01-01", null, null)],
                Observations: [new NamsObservation("o1", "conv-1", "summary", "2026-01-01", null)],
                RecentMessages: [new NamsMessage("m1", "hello", "user", "2026-01-01", "conv-1", null, null)]))
        };
        var service = CreateService(client);

        var result = await service.RecallAsync("conv-1", null, CancellationToken.None);

        result.Items.Should().HaveCount(3);
        result.Items[0].Should().BeEquivalentTo(new { SourceId = "r1", Category = NamsRecallCategory.Reflection, Content = "insight" });
        result.Items[1].Should().BeEquivalentTo(new { SourceId = "o1", Category = NamsRecallCategory.Observation, Content = "summary" });
        result.Items[2].Should().BeEquivalentTo(new { SourceId = "m1", Category = NamsRecallCategory.RecentMessage, Content = "hello", Role = "user" });
    }

    [Fact]
    public async Task RecallAsync_EntityWithNoDescription_FallsBackToName()
    {
        var client = new FakeNamsClient
        {
            OnSearchEntities = (_, _, _) => Task.FromResult<IReadOnlyList<NamsEntity>>(
                [new NamsEntity("e1", "Acme Corp", null, Description: null, null, null, null, null, null)])
        };
        var service = CreateService(client);

        var result = await service.RecallAsync("conv-1", "query", CancellationToken.None);

        result.Items.Single().Content.Should().Be("Acme Corp");
    }

    [Fact]
    public async Task RecallAsync_ExactOrdering_ReflectionsThenObservationsThenMessagesThenEntities()
    {
        var client = new FakeNamsClient
        {
            OnGetContext = _ => Task.FromResult(new NamsContext(
                Reflections: [new NamsReflection("r1", null, "x", null, null, null)],
                Observations: [new NamsObservation("o1", null, "x", null, null)],
                RecentMessages: [new NamsMessage("m1", "x", "user", null, null, null, null)])),
            OnSearchEntities = (_, _, _) => Task.FromResult<IReadOnlyList<NamsEntity>>(
                [new NamsEntity("e1", "Acme", null, "x", null, null, null, null, null)])
        };
        var service = CreateService(client);

        var result = await service.RecallAsync("conv-1", "query", CancellationToken.None);

        result.Items.Select(i => i.Category).Should().Equal(
            NamsRecallCategory.Reflection, NamsRecallCategory.Observation, NamsRecallCategory.RecentMessage, NamsRecallCategory.Entity);
    }

    [Theory]
    [InlineData("user", NamsRecallProvenance.UserProvided)]
    [InlineData("User", NamsRecallProvenance.UserProvided)] // case-insensitive -- NAMS's own casing isn't guaranteed
    [InlineData("assistant", NamsRecallProvenance.ModelGenerated)]
    [InlineData("tool", NamsRecallProvenance.ToolDerived)]
    [InlineData("system", NamsRecallProvenance.Untrusted)]
    [InlineData("something-unrecognized", NamsRecallProvenance.Untrusted)]
    public async Task RecallAsync_MessageRole_MapsToExpectedProvenance(string role, NamsRecallProvenance expected)
    {
        var client = new FakeNamsClient
        {
            OnGetContext = _ => Task.FromResult(new NamsContext(
                [], [], [new NamsMessage("m1", "x", role, null, null, null, null)]))
        };
        var service = CreateService(client);

        var result = await service.RecallAsync("conv-1", null, CancellationToken.None);

        result.Items.Single().Provenance.Should().Be(expected);
    }

    [Fact]
    public async Task RecallAsync_ReflectionsAndObservationsAndEntities_MapToModelGenerated_NeverApplicationTrustedOrVerifiedExternal()
    {
        var client = new FakeNamsClient
        {
            OnGetContext = _ => Task.FromResult(new NamsContext(
                Reflections: [new NamsReflection("r1", null, "x", null, null, null)],
                Observations: [new NamsObservation("o1", null, "x", null, null)],
                RecentMessages: [])),
            OnSearchEntities = (_, _, _) => Task.FromResult<IReadOnlyList<NamsEntity>>(
                [new NamsEntity("e1", "Acme", null, "x", null, null, null, null, null)])
        };
        var service = CreateService(client);

        var result = await service.RecallAsync("conv-1", "query", CancellationToken.None);

        result.Items.Should().OnlyContain(i => i.Provenance == NamsRecallProvenance.ModelGenerated);
        result.Items.Should().NotContain(i => i.Provenance == NamsRecallProvenance.ApplicationTrusted || i.Provenance == NamsRecallProvenance.VerifiedExternal);
    }

    [Fact]
    public async Task RecallAsync_DuplicateSourceId_CollapsesToOneItem()
    {
        var client = new FakeNamsClient
        {
            OnGetContext = _ => Task.FromResult(new NamsContext(
                [], [], [new NamsMessage("dup-1", "hello", "user", null, null, null, null)])),
            OnSearchEntities = (_, _, _) => Task.FromResult<IReadOnlyList<NamsEntity>>(
                [new NamsEntity("dup-1", "SameIdAsMessage", null, null, null, null, null, null, null)])
        };
        var service = CreateService(client);

        var result = await service.RecallAsync("conv-1", "query", CancellationToken.None);

        result.Items.Should().ContainSingle(); // first occurrence (the message) wins, entity dropped as a duplicate
        result.Items.Single().Category.Should().Be(NamsRecallCategory.RecentMessage);
    }

    [Fact]
    public async Task RecallAsync_CharacterBudgetExceeded_TruncatesAndMarksPartial()
    {
        var client = new FakeNamsClient
        {
            OnGetContext = _ => Task.FromResult(new NamsContext(
                Reflections: [new NamsReflection("r1", null, new string('a', 10), null, null, null)],
                Observations: [new NamsObservation("o1", null, new string('b', 10), null, null)],
                RecentMessages: []))
        };
        var service = CreateService(client, new NamsRecallOptions { MaxTotalCharacters = 15 });

        var result = await service.RecallAsync("conv-1", null, CancellationToken.None);

        result.Items.Should().ContainSingle(); // only the reflection fits
        result.IsPartial.Should().BeTrue();
    }

    [Fact]
    public async Task RecallAsync_EmptyContext_ReturnsEmptyItems()
    {
        var service = CreateService(new FakeNamsClient());

        var result = await service.RecallAsync("conv-1", null, CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.IsPartial.Should().BeFalse();
    }

    [Fact]
    public async Task RecallAsync_ContextRetrievalTransientFailure_DegradesWithWarning_DoesNotThrow()
    {
        var client = new FakeNamsClient
        {
            OnGetContext = _ => throw new NamsOperationException(NamsFailureKind.ServerError, "boom")
        };
        var service = CreateService(client);

        var result = await service.RecallAsync("conv-1", null, CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.IsPartial.Should().BeTrue();
        result.Warnings.Should().ContainSingle(w => w.Category == "context");
    }

    [Fact]
    public async Task RecallAsync_EntitySearchTransientFailure_DoesNotDiscardContextResults()
    {
        var client = new FakeNamsClient
        {
            OnGetContext = _ => Task.FromResult(new NamsContext(
                [], [], [new NamsMessage("m1", "hello", "user", null, null, null, null)])),
            OnSearchEntities = (_, _, _) => throw new NamsOperationException(NamsFailureKind.ServerError, "boom")
        };
        var service = CreateService(client);

        var result = await service.RecallAsync("conv-1", "query", CancellationToken.None);

        result.Items.Should().ContainSingle(); // the message survives despite the entity search failing
        result.IsPartial.Should().BeTrue();
        result.Warnings.Should().ContainSingle(w => w.Category == "entity");
    }

    [Theory]
    [InlineData(nameof(NamsFailureKind.Authentication))]
    [InlineData(nameof(NamsFailureKind.Authorization))]
    public async Task RecallAsync_IdentitySecurityFailure_Propagates_DoesNotDegradeSilently(string failureKindName)
    {
        var failureKind = Enum.Parse<NamsFailureKind>(failureKindName);
        var client = new FakeNamsClient
        {
            OnGetContext = _ => throw new NamsOperationException(failureKind, "denied")
        };
        var service = CreateService(client);

        var act = () => service.RecallAsync("conv-1", null, CancellationToken.None);

        await act.Should().ThrowAsync<NamsOperationException>();
    }

    [Fact]
    public async Task RecallAsync_Cancellation_Propagates()
    {
        var client = new FakeNamsClient();
        var service = CreateService(client);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => service.RecallAsync("conv-1", null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RecallAsync_NoQueryText_NeverCallsEntitySearch()
    {
        var client = new FakeNamsClient();
        var service = CreateService(client);

        await service.RecallAsync("conv-1", null, CancellationToken.None);
        await service.RecallAsync("conv-1", "   ", CancellationToken.None);

        client.SearchEntitiesCallCount.Should().Be(0);
    }

    [Fact]
    public async Task RecallAsync_EntitySearchDisabled_NeverCallsEntitySearch_EvenWithQuery()
    {
        var client = new FakeNamsClient();
        var service = CreateService(client, new NamsRecallOptions { IncludeEntitySearch = false });

        await service.RecallAsync("conv-1", "query", CancellationToken.None);

        client.SearchEntitiesCallCount.Should().Be(0);
    }
}
