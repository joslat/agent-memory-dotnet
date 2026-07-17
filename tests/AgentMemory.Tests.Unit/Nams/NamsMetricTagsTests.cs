using System.Diagnostics;
using FluentAssertions;
using AgentMemory.Nams.Domain;
using AgentMemory.Nams.Observability;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsMetricTagsTests
{
    [Fact]
    public void Operation_IncludesBackendAndOperation()
    {
        var tags = NamsMetricTags.Operation("get_context");

        var dict = ToDictionary(tags);
        dict["backend"].Should().Be("nams");
        dict["operation"].Should().Be("get_context");
        dict.Should().HaveCount(2);
    }

    [Fact]
    public void OperationStatus_IncludesBackendOperationAndStatus()
    {
        var tags = NamsMetricTags.OperationStatus("store_turn", "succeeded");

        var dict = ToDictionary(tags);
        dict["backend"].Should().Be("nams");
        dict["operation"].Should().Be("store_turn");
        dict["status"].Should().Be("succeeded");
        dict.Should().HaveCount(3);
    }

    [Fact]
    public void OperationFailureKind_IncludesBackendOperationAndFailureKind()
    {
        var tags = NamsMetricTags.OperationFailureKind("search_entities", NamsFailureKind.RateLimited);

        var dict = ToDictionary(tags);
        dict["backend"].Should().Be("nams");
        dict["operation"].Should().Be("search_entities");
        dict["failure_kind"].Should().Be("RateLimited");
        dict.Should().HaveCount(3);
    }

    private static Dictionary<string, object?> ToDictionary(TagList tags)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var tag in tags)
            dict[tag.Key] = tag.Value;
        return dict;
    }
}
