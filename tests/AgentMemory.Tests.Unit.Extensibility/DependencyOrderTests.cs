using AgentMemory.Shared;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class DependencyOrderTests
{
    [Fact]
    public void ClosureIncludesDependenciesAndPreservesOrdinalReadyBatches()
    {
        var graph = new Dictionary<string, string[]> { ["a"] = ["b"], ["b"] = [], ["z"] = [] };
        Resolve(graph, ["a", "z"]).Should().Equal("b", "z", "a");
        Resolve(graph, ["z", "a", "a"]).Should().Equal("b", "z", "a");
    }

    [Fact]
    public void MissingDependencyNamesTheRequiringNode()
    {
        var graph = new Dictionary<string, string[]> { ["a"] = ["missing"] };
        var resolve = () => Resolve(graph, ["a"]);
        resolve.Should().Throw<InvalidOperationException>().WithMessage("a needs missing");
    }

    [Fact]
    public void CycleReportsSortedBlockedNodes()
    {
        var graph = new Dictionary<string, string[]> { ["z"] = ["a"], ["a"] = ["z"], ["later"] = ["z"] };
        var resolve = () => Resolve(graph, ["later"]);
        resolve.Should().Throw<InvalidOperationException>().WithMessage("cycle: a,later,z");
    }

    [Fact]
    public void EmptyRootsDoNotReadDeclarations()
    {
        var calls = 0;
        var result = DependencyOrder.Resolve(new Dictionary<string, string> { ["a"] = "a" }, [],
            _ => { calls++; return []; }, (_, _) => new InvalidOperationException(), _ => new InvalidOperationException());
        result.Should().BeEmpty();
        calls.Should().Be(0);
    }

    [Fact]
    public void DeepClosureDoesNotDependOnTheCallStack()
    {
        var graph = Enumerable.Range(0, 2048).ToDictionary(i => i.ToString("D4"),
            i => i == 0 ? Array.Empty<string>() : new[] { (i - 1).ToString("D4") });
        var result = Resolve(graph, ["2047"]);
        result.Count.Should().Be(2048);
        result[0].Should().Be("0000");
        result[^1].Should().Be("2047");
    }

    private static IReadOnlyList<string> Resolve(Dictionary<string, string[]> graph, string[] roots) =>
        DependencyOrder.Resolve(graph.Keys.ToDictionary(k => k, k => k), roots, id => graph[id],
            (from, to) => new InvalidOperationException($"{from} needs {to}"),
            ids => new InvalidOperationException("cycle: " + string.Join(",", ids)));
}
