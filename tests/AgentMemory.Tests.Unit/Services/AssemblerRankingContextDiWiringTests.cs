using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// Guards that the DI-resolved <see cref="IMemoryContextAssembler"/> is wired with the AsyncLocal ranking
/// context the long-term repositories read — i.e. that the D3 per-request query intent (Latest/Analog)
/// actually reaches ranking in a real container. The bug this guards: the factory passed
/// <c>rankingContext: null</c>, so <c>RecallOptions.Intent</c> was silently inert in every deployment, and
/// the existing assembler unit test could not catch it (it constructs the assembler by hand with a context).
/// </summary>
public sealed class AssemblerRankingContextDiWiringTests
{
    [Fact]
    public async Task DiResolvedAssembler_PublishesQueryIntentRanking_IntoTheContextRepositoriesRead()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryCore(_ => { });

        // Replace the layer services (Core's real impls need repositories that aren't registered here) with
        // mocks so the assembler resolves; the embedding orchestrator returns a non-empty vector so the
        // long-term searches actually run.
        var shortTerm = Substitute.For<IShortTermMemoryService>();
        shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));
        var reasoning = Substitute.For<IReasoningMemoryService>();
        reasoning.SearchSimilarTracesAsync(Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>(Array.Empty<ReasoningTrace>()));
        var embed = Substitute.For<IEmbeddingOrchestrator>();
        embed.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new float[] { 1f }));
        var longTerm = Substitute.For<ILongTermMemoryService>();
        longTerm.SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));
        longTerm.SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));

        services.Replace(ServiceDescriptor.Scoped(_ => shortTerm));
        services.Replace(ServiceDescriptor.Scoped(_ => reasoning));
        services.Replace(ServiceDescriptor.Scoped(_ => embed));
        services.Replace(ServiceDescriptor.Scoped(_ => longTerm));

        var provider = services.BuildServiceProvider();
        var rankingContext = provider.GetRequiredService<IMemoryRankingContext>();

        // Capture what the repositories WOULD read (the ambient ranking) at the moment the assembler issues
        // the long-term fact search.
        MemoryRankingOptions? observedDuringSearch = null;
        longTerm.SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                observedDuringSearch = rankingContext.Current;
                return Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>());
            });

        using var scope = provider.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<IMemoryContextAssembler>();

        await assembler.AssembleContextAsync(new RecallRequest
        {
            SessionId = "s",
            Query = "q",
            Options = new RecallOptions { Intent = RankingIntent.Latest }
        });

        observedDuringSearch.Should().NotBeNull(
            "the DI-wired assembler must publish the per-request intent into the ranking context the repositories read");
        observedDuringSearch!.EffectiveRecencyWeight.Should().BeGreaterThan(0, "Latest raises the recency weight");
        rankingContext.Current.Should().BeNull("the override must be reset after the long-term searches, never leaking");
    }
}
