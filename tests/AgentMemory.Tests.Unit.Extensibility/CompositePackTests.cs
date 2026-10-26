using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Coding;
using AgentMemory.Packs.Decisions;
using AgentMemory.Packs.Repository;
using AgentMemory.Packs.Work;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Composite packs (KP-composite): one pack made of packs. Members stay whole modules with their own priority and switch;
/// a composite cannot add what it does not name, and the host can leave a member out or move it.
/// </summary>
public sealed class CompositePackTests
{
    [Fact]
    public void The_coding_composite_adds_decisions_repository_code_map_and_work_with_their_priorities()
    {
        var builder = new ModuleSetBuilder();

        builder.AddComposite<CodingPack>();

        builder.Modules.Select(m => m.Manifest.Id).Should().Equal(DecisionsPack.Id, RepositoryPack.Id, CodeMapPack.Id, WorkPack.Id);
        builder.Priorities.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [DecisionsPack.Id] = 10, [RepositoryPack.Id] = 20, [CodeMapPack.Id] = 30, [WorkPack.Id] = 40,
        });
        CodingPack.Manifest.Validate().Should().BeEmpty();
        CodingPack.Manifest.Display!.Title.Should().Be("Coding");
    }

    [Fact]
    public void A_host_can_leave_a_member_out_or_move_it_but_not_name_one_the_composite_lacks()
    {
        var builder = new ModuleSetBuilder();
        builder.AddComposite<CodingPack>(o => o.Disable(RepositoryPack.Id).Prioritize(CodeMapPack.Id, 5));

        builder.Modules.Select(m => m.Manifest.Id).Should().Equal(DecisionsPack.Id, CodeMapPack.Id, WorkPack.Id);
        builder.Priorities[CodeMapPack.Id].Should().Be(5);

        FluentActions.Invoking(() => new ModuleSetBuilder().AddComposite<CodingPack>(o => o.Disable("io.example.nothing")))
            .Should().Throw<InvalidOperationException>().WithMessage("*has no member io.example.nothing*");
    }

    [Theory]
    [InlineData("""{"format":"agentmemory.composite/v1alpha1","id":"io.example.c","version":"1.0.0","members":[]}""", "at least one member")]
    [InlineData("""{"format":"agentmemory.composite/v1alpha1","id":"io.example.c","version":"1.0.0","members":[{"id":"io.example.a","version":"[1,2)"},{"id":"io.example.a","version":"[1,2)"}]}""", "named twice")]
    [InlineData("""{"format":"agentmemory.composite/v1alpha1","id":"io.example.c","version":"1.0.0","members":[{"id":"io.example.c","version":"[1,2)"}]}""", "cannot name itself")]
    [InlineData("""{"format":"agentmemory.composite/v1alpha1","id":"io.example.c","version":"1.0.0","members":[{"id":"io.example.a","version":"[1,2)","priority":20000}]}""", "0 to 10,000")]
    public void A_composite_manifest_that_names_nothing_repeats_or_contains_itself_is_refused(string json, string problem) =>
        CompositeManifest.Parse(json).Validate().Should().Contain(p => p.Contains(problem, StringComparison.Ordinal));

    [Fact]
    public void A_member_switched_off_by_default_comes_in_only_when_the_host_enables_it()
    {
        var json = """{"format":"agentmemory.composite/v1alpha1","id":"io.example.c","version":"1.0.0","members":[{"id":"io.example.a","version":"[1,2)","enabled":false,"priority":7}]}""";
        var member = CompositeManifest.Parse(json).Members.Single();

        member.Enabled.Should().BeFalse();
        member.Priority.Should().Be(7);
        FluentActions.Invoking(() => CompositeManifest.Parse(json.Replace("agentmemory.composite/v1alpha1", "agentmemory.module/v1alpha1", StringComparison.Ordinal)))
            .Should().Throw<FormatException>().WithMessage("*composite format*");
    }
}
