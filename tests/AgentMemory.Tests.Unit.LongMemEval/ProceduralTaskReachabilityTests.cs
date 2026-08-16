using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 26.1. Every procedural task shape must be runnable by the harness.
/// </summary>
/// <remarks>
/// <para>
/// <b>This guard exists because its absence produced the exact defect this track catalogues, in this
/// track's own code.</b> <c>ProceduralIncidentTask</c> was written, given seven validity tests, and
/// committed with a message claiming the procedural sample had gone from one task to two — while
/// <c>ProceduralBenefitProgram</c> still constructed <c>ProceduralBenchmarkTask</c> in three hardcoded
/// places. The task was complete, tested, and reachable by nothing. The claim in that commit message
/// was false.
/// </para>
/// <para>
/// Unit tests could not catch it, because they tested the task directly. Only a test about
/// <i>reachability</i> can, which is the same lesson as the rerankers, the typed-report stack, and the
/// per-memory-type breakdown: a feature is not shipped when it compiles and passes, it is shipped when
/// something can reach it.
/// </para>
/// </remarks>
public sealed class ProceduralTaskReachabilityTests
{
    [Fact]
    public void EveryListedTaskCanBeConstructedByName()
    {
        // Red before the fix: "incident" threw, because the factory did not exist and the harness
        // named its task concretely.
        ProceduralTasks.Names.Should().NotBeEmpty();

        foreach (var name in ProceduralTasks.Names)
        {
            var task = ProceduralTasks.Create(name);
            task.Prompt.Should().NotBeNullOrWhiteSpace($"'{name}' must give the agent something to do");
            task.CreateTools().Should().NotBeEmpty($"'{name}' must expose tools");
        }
    }

    [Fact]
    public void EveryImplementedTaskIsListed()
    {
        // THE drift guard, and the direction that actually failed. A task added to the assembly but
        // left out of the factory is invisible: it compiles, its own tests pass, and no run can select
        // it. Reflected rather than listed so it cannot go stale.
        var implemented = typeof(ProceduralTasks).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.IsInterface && type.IsAssignableTo(typeof(IProceduralTask)))
            .ToList();

        implemented.Should().NotBeEmpty();

        var reachable = ProceduralTasks.Names
            .Select(name => ProceduralTasks.Create(name).GetType())
            .ToHashSet();

        implemented.Should().BeSubsetOf(reachable,
            "an IProceduralTask the factory cannot produce is a benchmark nobody can run");
    }

    [Fact]
    public void TheTaskShapesAreActuallyDifferent()
    {
        // Two names resolving to equivalent environments would double the apparent sample without
        // adding evidence: n=2 tasks that measure the same thing is still n=1.
        var prompts = ProceduralTasks.Names
            .Select(name => ProceduralTasks.Create(name).Prompt)
            .ToList();

        prompts.Should().OnlyHaveUniqueItems();

        var toolNames = ProceduralTasks.Names
            .Select(name => ProceduralTasks.Create(name).CreateTools()
                .OfType<Microsoft.Extensions.AI.AIFunction>()
                .Select(tool => tool.Name)
                .ToHashSet())
            .ToList();

        // Decoys may overlap in spirit but the real chains must not be the same set of tools.
        toolNames[0].Should().NotBeEquivalentTo(toolNames[1],
            "a second task that renamed the first would measure the same thing twice");
    }

    [Fact]
    public void AnUnknownTaskNameIsRefusedRatherThanDefaulted()
    {
        // Silently falling back to the rail task would report a second-task result that was really the
        // first task run again -- the most expensive possible way to get a wrong answer here.
        var create = () => ProceduralTasks.Create("does-not-exist");

        create.Should().Throw<ArgumentException>().WithMessage("*must be one of*");
    }
}
