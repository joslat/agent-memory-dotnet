using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// Direct coverage for the Core <see cref="MemoryQueryFacade"/> (task 3.8): the search/compose/format
/// business logic the framework adapters delegate to. Validation, success, error-as-failure, and
/// cancellation propagation are pinned here.
/// </summary>
public sealed class MemoryQueryFacadeTests
{
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGenerator = Substitute.For<IIdGenerator>();

    private MemoryQueryFacade CreateSut()
    {
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[8]);
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _idGenerator.GenerateId().Returns("id-1");
        _longTerm.SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));
        _longTerm.SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>()));
        _longTerm.SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));
        return new MemoryQueryFacade(_longTerm, _reasoning, _embeddings, _clock, _idGenerator,
            NullLogger<MemoryQueryFacade>.Instance);
    }

    private MemoryQueryFacade CreateSutWithOwner(string? userId)
    {
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[8]);
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _idGenerator.GenerateId().Returns("id-1");
        _longTerm.SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));
        _longTerm.SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>()));
        _longTerm.SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));
        _longTerm.AddFactAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Fact>()));

        var ownerContext = Substitute.For<IMemoryOwnerContext>();
        ownerContext.UserId.Returns(userId);
        return new MemoryQueryFacade(_longTerm, _reasoning, _embeddings, _clock, _idGenerator,
            NullLogger<MemoryQueryFacade>.Instance, ownerContext);
    }

    [Fact]
    public async Task SearchMemoryAsync_WithOwnerContext_ScopesSearchByOwner()
    {
        var sut = CreateSutWithOwner("alice");

        await sut.SearchMemoryAsync("anything");

        await _longTerm.Received(1).SearchEntitiesAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Is<MemoryScope?>(s => s != null && s.OwnerId == "alice"), Arg.Any<CancellationToken>());
        await _longTerm.Received(1).SearchFactsAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Is<MemoryScope?>(s => s != null && s.OwnerId == "alice"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchMemoryAsync_NoOwnerContext_PassesNullScope()
    {
        var sut = CreateSutWithOwner(null);

        await sut.SearchMemoryAsync("anything");

        await _longTerm.Received(1).SearchEntitiesAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Is<MemoryScope?>(s => s == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RememberFactAsync_WithOwnerContext_StampsOwner()
    {
        var sut = CreateSutWithOwner("alice");

        await sut.RememberFactAsync("Alice", "likes", "tea");

        await _longTerm.Received(1).AddFactAsync(
            Arg.Is<Fact>(f => f.OwnerId == "alice"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchMemoryAsync_EmptyQuery_ReturnsValidationFailure()
    {
        var result = await CreateSut().SearchMemoryAsync("   ");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("search_memory");
    }

    [Fact]
    public async Task SearchMemoryAsync_NoResults_ReturnsSuccessWithMessage()
    {
        var result = await CreateSut().SearchMemoryAsync("anything");

        result.Success.Should().BeTrue();
        result.Text.Should().Be("No results found.");
    }

    [Fact]
    public async Task RememberFactAsync_PersistsAndReportsSuccess()
    {
        _longTerm.AddFactAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Fact>()));

        var result = await CreateSut().RememberFactAsync("Alice", "works_at", "Acme");

        result.Success.Should().BeTrue();
        result.Text.Should().Contain("Alice works_at Acme");
        await _longTerm.Received(1).AddFactAsync(
            Arg.Is<Fact>(f => f.Subject == "Alice" && f.Predicate == "works_at" && f.Object == "Acme"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecallPreferencesAsync_NoCategoryNoQuery_ReturnsValidationFailure()
    {
        var result = await CreateSut().RecallPreferencesAsync(category: null, query: null);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("recall_preferences");
    }

    [Fact]
    public async Task SearchMemoryAsync_OnError_ReturnsFailureWithMessage()
    {
        var sut = CreateSut();
        // Override the default embedding stub set up in CreateSut.
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("embeddings down"));

        var result = await sut.SearchMemoryAsync("q");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("embeddings down");
    }

    [Fact]
    public async Task SearchMemoryAsync_Cancellation_PropagatesInsteadOfMaskingAsFailure()
    {
        var sut = CreateSut();
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var act = async () => await sut.SearchMemoryAsync("q");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
