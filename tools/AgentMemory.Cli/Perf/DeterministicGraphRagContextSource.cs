using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// Scenario-only GraphRAG source with a known result and delay. It lets the harness prove that the
/// optional path ran without coupling a GraphRAG orchestration measurement to a second search/index
/// implementation whose own behavior belongs in integration tests.
/// </summary>
internal sealed class DeterministicGraphRagContextSource : IGraphRagContextSource
{
    internal const long DelayMilliseconds = 300;
    internal const string FirstMarker =
        "Project Atlas depends on the Beacon identity service.";
    internal const string SecondMarker =
        "The platform team owns Beacon and its incident response.";

    public async Task<GraphRagContextResult> GetContextAsync(
        GraphRagContextRequest request,
        CancellationToken cancellationToken = default)
    {
        PerfCollector.Current?.Add("graphrag.calls");

        await Task.Delay(
                TimeSpan.FromMilliseconds(DelayMilliseconds),
                cancellationToken)
            .ConfigureAwait(false);

        PerfCollector.Current?.Add("injected.graphrag_delay.calls");
        PerfCollector.Current?.Add(
            "injected.graphrag_delay.ms",
            DelayMilliseconds);

        IReadOnlyList<GraphRagContextItem> all =
        [
            new()
            {
                Text = FirstMarker,
                Score = 1.0,
                Metadata = new Dictionary<string, object>
                {
                    ["source"] = "deterministic-perf-fixture",
                },
            },
            new()
            {
                Text = SecondMarker,
                Score = 0.9,
                Metadata = new Dictionary<string, object>
                {
                    ["source"] = "deterministic-perf-fixture",
                },
            },
        ];
        var items = all.Take(Math.Max(0, request.TopK)).ToList();
        PerfCollector.Current?.Add("items.graphrag", items.Count);
        return new GraphRagContextResult { Items = items };
    }
}
