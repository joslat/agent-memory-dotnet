using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Configuration;

/// <summary>
/// 25.2. A caller who does not specify recall options gets the <b>application's</b> configured
/// defaults, not the library's.
/// </summary>
/// <remarks>
/// <para>
/// <c>RecallRequest.Options</c> defaults to the static <c>RecallOptions.Default</c> singleton, and the
/// assembler read it directly. So <c>MemoryOptions.Recall</c> — the option whose entire purpose is to
/// configure recall — was consulted by almost nothing: it bound, it validated, and any direct
/// <c>RecallAsync</c> call ignored it completely.
/// </para>
/// <para>
/// The fix substitutes on <b>reference equality</b> with the singleton, which is true exactly when the
/// caller left the property alone. It is a no-op for anyone who has configured nothing, because
/// <c>MemoryOptions.Recall</c> itself defaults to that same instance — the unconfigured path stays
/// byte-identical, which is what makes this safe to change under SemVer.
/// </para>
/// </remarks>
public sealed class ConfiguredRecallDefaultTests
{
    [Fact]
    public async Task AnUnspecifiedRequestUsesTheConfiguredRecallOptions()
    {
        // Red before 25.2: MaxFacts here was 10 (the library default) however the host was configured.
        var (assembler, longTerm) = Create(new MemoryOptions
        {
            Recall = RecallOptions.Default with { MaxFacts = 42, MinSimilarityScore = 0.31 },
        });

        await assembler.AssembleContextAsync(new RecallRequest { SessionId = "s", Query = "q" });

        await longTerm.Received().SearchFactsAsync(
            Arg.Any<float[]>(), 42, 0.31, Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnExplicitRequestStillWins()
    {
        // Configuration is a default, not an override. A caller who names its own options must get
        // them, or per-call tuning would silently stop working the moment a host configured anything.
        var (assembler, longTerm) = Create(new MemoryOptions
        {
            Recall = RecallOptions.Default with { MaxFacts = 42 },
        });

        await assembler.AssembleContextAsync(new RecallRequest
        {
            SessionId = "s",
            Query = "q",
            Options = RecallOptions.Default with { MaxFacts = 3, MinSimilarityScore = 0.9 },
        });

        await longTerm.Received().SearchFactsAsync(
            Arg.Any<float[]>(), 3, 0.9, Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnconfiguredHostBehavesExactlyAsBefore()
    {
        // THE compatibility assertion. With nothing configured, MemoryOptions.Recall IS
        // RecallOptions.Default, so the substitution changes nothing and every sealed measurement
        // taken before this change remains comparable with everything taken after it.
        var (assembler, longTerm) = Create(new MemoryOptions());

        await assembler.AssembleContextAsync(new RecallRequest { SessionId = "s", Query = "q" });

        await longTerm.Received().SearchFactsAsync(
            Arg.Any<float[]>(),
            RecallOptions.Default.MaxFacts,
            RecallOptions.Default.MinSimilarityScore,
            Arg.Any<MemoryScope>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The assembler over substituted collaborators. Asserted at the <c>ILongTermMemoryService</c>
    /// seam, which is where the recall caps and the similarity threshold actually arrive.
    /// </summary>
    private static (MemoryContextAssembler Assembler, ILongTermMemoryService LongTerm) Create(
        MemoryOptions options)
    {
        var longTerm = Substitute.For<ILongTermMemoryService>();
        longTerm.SearchFactsAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var shortTerm = Substitute.For<IShortTermMemoryService>();
        shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[8]);

        var assembler = new MemoryContextAssembler(
            shortTerm,
            longTerm,
            Substitute.For<IReasoningMemoryService>(),
            graphRag: null,
            embeddings,
            Substitute.For<IClock>(),
            Options.Create(options),
            NullLogger<MemoryContextAssembler>.Instance,
            new DefaultMemoryIsolationPolicy(
                Options.Create(options.Isolation),
                NullLogger<DefaultMemoryIsolationPolicy>.Instance));

        return (assembler, longTerm);
    }
}
