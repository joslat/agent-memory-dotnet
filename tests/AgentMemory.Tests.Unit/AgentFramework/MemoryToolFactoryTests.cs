using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework.Tools;
using AgentMemory.Core.Services;
using NSubstitute;

namespace AgentMemory.Tests.Unit.AgentFramework;

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
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[384]);

        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _idGenerator.GenerateId().Returns("test-id");

        _longTermService
            .SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));
        _longTermService
            .SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>()));
        _longTermService
            .SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));
        _longTermService
            .GetPreferencesByCategoryAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));
        _longTermService
            .AddPreferenceAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Preference>()));
        _longTermService
            .AddFactAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Fact>()));
        _reasoningService
            .SearchSimilarTracesAsync(Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>(Array.Empty<ReasoningTrace>()));
    }

    private MemoryToolFactory CreateSut()
    {
        // The factory is a thin adapter over the real Core facade; wire the facade with the mocked
        // services so the existing Received(...) assertions still observe the underlying calls.
        var facade = new MemoryQueryFacade(
            _longTermService, _reasoningService, _embeddingOrchestrator, _clock, _idGenerator,
            NullLogger<MemoryQueryFacade>.Instance,
            new DefaultMemoryIsolationPolicy(Microsoft.Extensions.Options.Options.Create(new MemoryIsolationOptions()), NullLogger<DefaultMemoryIsolationPolicy>.Instance));
        return new MemoryToolFactory(facade);
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

        await _embeddingOrchestrator.Received(1).EmbedAsync("find Alice", Arg.Any<CancellationToken>());
        await _longTermService.Received(1).SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
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
}
