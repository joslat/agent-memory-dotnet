using FluentAssertions;
using AgentMemory.Neo4j.Retrieval.Internal;
using Neo4j.Driver;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.GraphRagAdapter;

/// <summary>
/// K10. What does GraphRAG put in the prompt when pointed at a memory-native index?
/// </summary>
/// <remarks>
/// <see cref="RetrieverRecordMapper.FromNodeScore"/> takes its display text from the node's
/// <c>text</c> property, falls back to <c>content</c>, and finally to <c>node.ToString()</c>. None of
/// the memory layer's own node kinds carry <c>text</c> or <c>content</c>: a <c>Fact</c> has
/// <c>subject</c>/<c>predicate</c>/<c>object</c> and, like every embedded kind, an <c>embedding</c>.
/// The last-resort branch is therefore the <i>only</i> branch reachable for a Fact.
/// <para>
/// These tests pin what this code decides — which branch is taken, and what survives the mapping.
/// They deliberately do not assert what the serialised node <i>looks like</i>: that is the Neo4j
/// driver's <c>Node.ToString()</c>, not ours, and a fake node's rendering would only be evidence
/// about the fake. The real rendering is observed in the live K6 run.
/// </para>
/// </remarks>
public sealed class DefaultProjectionTests
{
    private const string NodeRendering = "<<whatever the driver prints for the whole node>>";

    [Fact]
    public void AFactNodeFallsBackToSerialisingTheWholeNode()
    {
        var item = RetrieverRecordMapper.FromNodeScore(FactRecord());

        // Not the readable triple a reader would expect from a "context passage" - the prompt gets
        // the driver's dump of the entire node, embedding property included.
        item.Content.Should().NotContain("Alice likes coffee");
        item.Content.Should().Be(NodeRendering);
    }

    [Fact]
    public void APropertyNamedTextWouldHaveBeenPreferred()
    {
        // The control: the fallback above is a consequence of Fact's property set, not of the mapper
        // being unable to find text. Nothing needs fixing in the mapper.
        var item = RetrieverRecordMapper.FromNodeScore(FactRecord(("text", "Alice likes coffee")));

        item.Content.Should().Be("Alice likes coffee");
    }

    [Fact]
    public void NoNodeIdentitySurvivesTheMapping()
    {
        // Metadata carries score and nothing else, so an item cannot be traced back to the node it
        // came from. This is why GraphRagContextItem.SourceNodeIds stays empty on the real Neo4j
        // path however carefully the assembler preserves the items (K4).
        var item = RetrieverRecordMapper.FromNodeScore(FactRecord());

        item.Metadata.Should().ContainKey("score");
        item.Metadata!.Keys.Should().NotContain("id");
    }

    private static IRecord FactRecord(params (string Key, object Value)[] extraProperties)
    {
        var properties = new Dictionary<string, object>
        {
            ["id"] = "fact-1",
            ["subject"] = "Alice",
            ["predicate"] = "likes",
            ["object"] = "coffee",
            ["embedding"] = new List<object> { 0.101d, 0.202d, 0.303d }
        };
        foreach (var (key, value) in extraProperties)
            properties[key] = value;

        var record = Substitute.For<IRecord>();
        record["node"].Returns(new StubNode(properties));
        record["score"].Returns(0.87d);
        return record;
    }

    /// <summary>A hand-written node, because ToString() cannot be stubbed on a substitute.</summary>
    private sealed class StubNode(IReadOnlyDictionary<string, object> properties) : INode
    {
        public IReadOnlyDictionary<string, object> Properties { get; } = properties;
        public object this[string key] => Properties[key];
        public IReadOnlyList<string> Labels { get; } = ["Fact"];
        public long Id => 1;
        public string ElementId => "4:x:1";
        public bool Equals(INode? other) => ReferenceEquals(this, other);
        public T Get<T>(string key) => (T)Properties[key];

        public bool TryGet<T>(string key, out T value)
        {
            if (Properties.TryGetValue(key, out var raw) && raw is T typed)
            {
                value = typed;
                return true;
            }

            value = default!;
            return false;
        }

        public override string ToString() => NodeRendering;
    }
}
