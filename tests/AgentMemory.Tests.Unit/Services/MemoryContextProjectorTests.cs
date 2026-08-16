using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services.Projection;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.2 step 3. The orchestrator: which features run, in what order, and when the answer is null.
/// </summary>
public sealed class MemoryContextProjectorTests
{
    private static ProjectionState State(MemoryProjectionOptions? options = null) => new()
    {
        Options = options ?? MemoryProjectionOptions.Default,
        Scope = null,
        Entities = [],
        Facts = [],
        Preferences = [],
        Traces = [],
        RecentMessages = [],
        RelevantMessages = [],
        EntityScores = [],
        FactScores = [],
        PreferenceScores = [],
        TraceScores = [],
    };

    [Fact]
    public async Task NoFeatureEnabledReturnsNull()
    {
        // THE off-state guarantee, at its source. A non-null-but-empty projection would still put all
        // three render surfaces on their projection-aware branch -- the branch that must not run for
        // the sealed prompt bytes to stay sealed.
        var projector = new MemoryContextProjector([new FakeFeature(enabled: false)]);

        (await projector.ProjectAsync(State(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task NoFeaturesRegisteredAtAllReturnsNull()
    {
        var projector = new MemoryContextProjector([]);

        (await projector.ProjectAsync(State(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AnEnabledFeatureThatContributesProducesAProjection()
    {
        var projector = new MemoryContextProjector(
            [new FakeFeature(enabled: true, contribute: s => s.Annotate("f1", a => a with { Score = 0.9 }))]);

        var projected = await projector.ProjectAsync(State(), CancellationToken.None);

        projected.Should().NotBeNull();
        projected!.Annotations.Should().ContainKey("f1");
        projected.Annotations["f1"].Score.Should().Be(0.9);
    }

    [Fact]
    public async Task AnEnabledFeatureThatContributesNothingStillReturnsNull()
    {
        // Enabled-but-silent renders identically to off, so putting every surface on its new code path
        // to produce byte-identical output would be risk with no benefit.
        var projector = new MemoryContextProjector([new FakeFeature(enabled: true)]);

        (await projector.ProjectAsync(State(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task OnlyEnabledFeaturesRun()
    {
        var off = new FakeFeature(enabled: false);
        var on = new FakeFeature(enabled: true, contribute: s => s.AddBlock(
            ProjectedBlockKind.NoDirectMatch, ProjectionSectionKeys.Facts, "x"));

        await new MemoryContextProjector([off, on]).ProjectAsync(State(), CancellationToken.None);

        off.Ran.Should().BeFalse();
        on.Ran.Should().BeTrue();
    }

    [Fact]
    public async Task FeaturesRunInRegistrationOrder()
    {
        // Order is fixed so the result is deterministic. Two features writing the same annotation in a
        // varying order would produce a context that differed between processes.
        var order = new List<string>();
        var first = new FakeFeature(enabled: true, contribute: _ => order.Add("first"));
        var second = new FakeFeature(enabled: true, contribute: _ => order.Add("second"));

        await new MemoryContextProjector([first, second]).ProjectAsync(State(), CancellationToken.None);

        order.Should().Equal("first", "second");
    }

    [Fact]
    public async Task TwoFeaturesContributeToOneAnnotationWithoutClobbering()
    {
        // A fact carries a score, a supersession note, a quote and a date, written by four different
        // features. Last-writer-wins here would silently drop three of them.
        var scorer = new FakeFeature(enabled: true, contribute: s => s.Annotate("f1", a => a with { Score = 0.72 }));
        var quoter = new FakeFeature(enabled: true, contribute: s => s.Annotate("f1", a => a with { SourceQuote = "he said so" }));

        var projected = await new MemoryContextProjector([scorer, quoter])
            .ProjectAsync(State(), CancellationToken.None);

        projected!.Annotations["f1"].Score.Should().Be(0.72);
        projected.Annotations["f1"].SourceQuote.Should().Be("he said so");
    }

    [Fact]
    public async Task CancellationIsObservedBetweenFeatures()
    {
        using var cts = new CancellationTokenSource();
        var projector = new MemoryContextProjector([new FakeFeature(enabled: true, contribute: _ => cts.Cancel()),
            new FakeFeature(enabled: true)]);

        var act = async () => await projector.ProjectAsync(State(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FakeFeature(bool enabled, Action<ProjectionState>? contribute = null) : IProjectionFeature
    {
        public bool Ran { get; private set; }

        public bool IsEnabled(MemoryProjectionOptions options) => enabled;

        public Task ApplyAsync(ProjectionState state, CancellationToken cancellationToken)
        {
            Ran = true;
            contribute?.Invoke(state);
            return Task.CompletedTask;
        }
    }
}
