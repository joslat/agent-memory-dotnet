using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.AgentFramework.Tools;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.AgentFramework;

public sealed class MemoryToolFactoryTests
{
    private readonly ILongTermMemoryService _longTermService;
    private readonly IReasoningMemoryService _reasoningService;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;

    public MemoryToolFactoryTests()
    {
        _longTermService = Substitute.For<ILongTermMemoryService>();
        _reasoningService = Substitute.For<IReasoningMemoryService>();
        _embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
        _clock = Substitute.For<IClock>();
        _idGenerator = Substitute.For<IIdGenerator>();

        _embeddingOrchestrator
            .EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);

        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _idGenerator.GenerateId().Returns("test-id");

        _longTermService
            .SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));
        _longTermService
            .SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>()));
        _longTermService
            .SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));
        _longTermService
            .GetPreferencesByCategoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));
        _longTermService
            .AddPreferenceAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Preference>()));
        _longTermService
            .AddFactAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Fact>()));
        _reasoningService
            .SearchSimilarTracesAsync(Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>(Array.Empty<ReasoningTrace>()));
    }

    private MemoryToolFactory CreateSut() => new(
        _longTermService, _reasoningService, _embeddingOrchestrator, _clock, _idGenerator,
        NullLogger<MemoryToolFactory>.Instance);

    private MemoryTool GetTool(string name) =>
#pragma warning disable CS0618
        CreateSut().CreateTools().Single(t => t.Name == name);
#pragma warning restore CS0618

    [Fact]
    public void CreateTools_Returns6Tools()
    {
#pragma warning disable CS0618
        var tools = CreateSut().CreateTools();
#pragma warning restore CS0618

        tools.Should().HaveCount(6);
        tools.Select(t => t.Name).Should().BeEquivalentTo(
            "search_memory", "remember_preference", "remember_fact",
            "recall_preferences", "search_knowledge", "find_similar_tasks");
    }

    // ── CreateAIFunctions ─────────────────────────────────────────────────────

    [Fact]
    public void CreateAIFunctions_Returns6AIFunctions()
    {
        var functions = CreateSut().CreateAIFunctions();

        functions.Should().HaveCount(6);
        functions.Select(f => f.Name).Should().BeEquivalentTo(
            "search_memory", "remember_preference", "remember_fact",
            "recall_preferences", "search_knowledge", "find_similar_tasks");
    }

    [Fact]
    public void CreateAIFunctions_AllHaveDescriptions()
    {
        var functions = CreateSut().CreateAIFunctions();

        foreach (var fn in functions)
            fn.Description.Should().NotBeNullOrWhiteSpace(
                because: $"'{fn.Name}' must have a description for schema generation");
    }

    [Fact]
    public async Task CreateAIFunctions_SearchMemory_InvokesEmbeddingAndSearch()
    {
        var fn = CreateSut().CreateAIFunctions().Single(f => f.Name == "search_memory");

        await fn.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(
            new Dictionary<string, object?> { ["query"] = "find Alice" }));

        await _embeddingOrchestrator.Received(1).EmbedQueryAsync("find Alice", Arg.Any<CancellationToken>());
        await _longTermService.Received(1).SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAIFunctions_RememberPreference_PersistsPreference()
    {
        var fn = CreateSut().CreateAIFunctions().Single(f => f.Name == "remember_preference");

        await fn.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(
            new Dictionary<string, object?> { ["preferenceText"] = "Prefers dark mode", ["category"] = "style" }));

        await _longTermService.Received(1).AddPreferenceAsync(
            Arg.Is<Preference>(p => p.Category == "style" && p.PreferenceText == "Prefers dark mode"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAIFunctions_RememberFact_PersistsFact()
    {
        var fn = CreateSut().CreateAIFunctions().Single(f => f.Name == "remember_fact");

        await fn.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(
            new Dictionary<string, object?> { ["subject"] = "Alice", ["predicate"] = "works_at", ["object"] = "Acme" }));

        await _longTermService.Received(1).AddFactAsync(
            Arg.Is<Fact>(f => f.Subject == "Alice" && f.Predicate == "works_at" && f.Object == "Acme"),
            Arg.Any<CancellationToken>());
    }

    // ── Legacy CreateTools tests (kept for backward compatibility) ────────────

    [Fact]
    public async Task SearchMemory_WithQuery_CallsEmbeddingAndSearch()
    {
        var tool = GetTool("search_memory");
        var request = new MemoryToolRequest { Query = "find Alice" };

        var response = await tool.ExecuteAsync(request, CancellationToken.None);

        response.Success.Should().BeTrue();
        await _embeddingOrchestrator.Received(1)
            .EmbedQueryAsync("find Alice", Arg.Any<CancellationToken>());
        await _longTermService.Received(1)
            .SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await _longTermService.Received(1)
            .SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await _longTermService.Received(1)
            .SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RememberPreference_CreatesAndPersists()
    {
        var tool = GetTool("remember_preference");
        var request = new MemoryToolRequest
        {
            Query = "Prefers dark mode",
            Parameters = new Dictionary<string, object> { ["category"] = "style" }
        };

        var response = await tool.ExecuteAsync(request, CancellationToken.None);

        response.Success.Should().BeTrue();
        await _longTermService.Received(1).AddPreferenceAsync(
            Arg.Is<Preference>(p => p.Category == "style" && p.PreferenceText == "Prefers dark mode"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RememberFact_CreatesAndPersists()
    {
        var tool = GetTool("remember_fact");
        var request = new MemoryToolRequest
        {
            Query = "Alice works at Acme",
            Parameters = new Dictionary<string, object>
            {
                ["subject"] = "Alice",
                ["predicate"] = "works_at",
                ["object"] = "Acme Corp"
            }
        };

        var response = await tool.ExecuteAsync(request, CancellationToken.None);

        response.Success.Should().BeTrue();
        await _longTermService.Received(1).AddFactAsync(
            Arg.Is<Fact>(f => f.Subject == "Alice" && f.Predicate == "works_at" && f.Object == "Acme Corp"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecallPreferences_WithCategory_FiltersResults()
    {
        var pref = new Preference
        {
            PreferenceId = "p-1", Category = "style",
            PreferenceText = "Prefers dark mode", Confidence = 1.0,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        _longTermService
            .GetPreferencesByCategoryAsync("style", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(new[] { pref }));

        var tool = GetTool("recall_preferences");
        var request = new MemoryToolRequest
        {
            Query = string.Empty,
            Parameters = new Dictionary<string, object> { ["category"] = "style" }
        };

        var response = await tool.ExecuteAsync(request, CancellationToken.None);

        response.Success.Should().BeTrue();
        await _longTermService.Received(1)
            .GetPreferencesByCategoryAsync("style", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecallPreferences_NoCategory_ReturnsAll()
    {
        var pref = new Preference
        {
            PreferenceId = "p-1", Category = "general",
            PreferenceText = "Prefers short answers", Confidence = 1.0,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        _longTermService
            .SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(new[] { pref }));

        var tool = GetTool("recall_preferences");
        var request = new MemoryToolRequest { Query = "all preferences" };

        var response = await tool.ExecuteAsync(request, CancellationToken.None);

        response.Success.Should().BeTrue();
        await _longTermService.Received(1)
            .SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchKnowledge_CallsEntitySearch()
    {
        var entity = new Entity
        {
            EntityId = "e-1", Name = "Alice", Type = "Person",
            Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow
        };
        _longTermService
            .SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(new[] { entity }));

        var tool = GetTool("search_knowledge");
        var request = new MemoryToolRequest { Query = "Alice" };

        var response = await tool.ExecuteAsync(request, CancellationToken.None);

        response.Success.Should().BeTrue();
        response.Result.Should().Contain("Alice");
        await _longTermService.Received(1)
            .SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindSimilarTasks_CallsTraceSearch()
    {
        var trace = new ReasoningTrace
        {
            TraceId = "t-1", SessionId = "s-1", Task = "Write a report",
            StartedAtUtc = DateTimeOffset.UtcNow, Success = true, Outcome = "Done"
        };
        _reasoningService
            .SearchSimilarTracesAsync(Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>(new[] { trace }));

        var tool = GetTool("find_similar_tasks");
        var request = new MemoryToolRequest { Query = "summarize document" };

        var response = await tool.ExecuteAsync(request, CancellationToken.None);

        response.Success.Should().BeTrue();
        await _reasoningService.Received(1)
            .SearchSimilarTracesAsync(Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryToolFactory_UsesEmbeddingOrchestrator_ForAllSearchTools()
    {
        // Verify all search tools use IEmbeddingOrchestrator (not raw IEmbeddingGenerator)
        var queries = new[] { "search_memory", "search_knowledge", "find_similar_tasks" };

        foreach (var toolName in queries)
        {
            _embeddingOrchestrator.ClearReceivedCalls();
            var tool = GetTool(toolName);
            var request = new MemoryToolRequest { Query = "test query" };

            await tool.ExecuteAsync(request, CancellationToken.None);

            await _embeddingOrchestrator.Received(1)
                .EmbedQueryAsync("test query", Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task RecallPreferences_NoCategory_UsesOrchestrator()
    {
        var tool = GetTool("recall_preferences");
        var request = new MemoryToolRequest { Query = "food preferences" };

        await tool.ExecuteAsync(request, CancellationToken.None);

        await _embeddingOrchestrator.Received(1)
            .EmbedQueryAsync("food preferences", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tool_OnError_ReturnsFailureResponse()
    {
        _embeddingOrchestrator
            .EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<float[]>(
                new InvalidOperationException("embedding service unavailable")));

        var tool = GetTool("search_memory");
        var request = new MemoryToolRequest { Query = "anything" };

        var response = await tool.ExecuteAsync(request, CancellationToken.None);

        response.Success.Should().BeFalse();
        response.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Tool_EmptyQuery_ReturnsFailure()
    {
        var tool = GetTool("search_memory");
        var request = new MemoryToolRequest { Query = string.Empty };

        var response = await tool.ExecuteAsync(request, CancellationToken.None);

        response.Success.Should().BeFalse();
        response.Error.Should().NotBeNullOrEmpty();
    }
}
