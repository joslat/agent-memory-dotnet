#pragma warning disable AMEXT001
using AgentMemory.Extensibility.Ampack;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Feature 17, R1: only a pack's status can be subscribed to, and a check names each subscribed status that changed, once.</summary>
public sealed class PackStatusWatchTests
{
    [Fact]
    public void A_check_names_each_subscribed_status_that_changed_once_and_nothing_else()
    {
        var states = new Dictionary<string, string> { ["a"] = "on", ["b"] = "on" };
        var watch = new PackStatusWatch();
        watch.Subscribe("ampack://status/a", id => states[id]).Should().BeTrue();
        watch.Subscribe("ampack://status/b", id => states[id]).Should().BeTrue();
        foreach (var other in new[] { "ampack://sha256:00/card", "ampack://status/", "ampack://status/a/b", "file:///x" })
            watch.Subscribe(other, id => states[id]).Should().BeFalse(other);

        watch.Changed(id => states[id]).Should().BeEmpty();
        states["a"] = "off";
        watch.Changed(id => states[id]).Should().Equal("ampack://status/a");
        watch.Changed(id => states[id]).Should().BeEmpty("a change is told once");

        watch.Unsubscribe("ampack://status/b");
        states["b"] = "off";
        watch.Changed(id => states[id]).Should().BeEmpty("an ended subscription is not told");
    }
}
