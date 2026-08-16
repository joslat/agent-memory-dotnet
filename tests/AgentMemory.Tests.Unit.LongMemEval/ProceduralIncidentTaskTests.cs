using System.Text.Json;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 26.1. The second procedural task must satisfy the same validity rules as the first.
/// </summary>
/// <remarks>
/// <para>
/// A procedural benchmark is only a benchmark if the shortest correct path is <b>discoverable but not
/// guessable</b>. Four of the rail task's seven attempts failed to discriminate — not because
/// procedural memory does not work, but because a competent model ordered the calls cold and there was
/// no discovery cost to save. Those failures are what these assertions encode.
/// </para>
/// <para>
/// Provider-free: every rule here is checkable without a model, which is the point. Discovering that a
/// task cannot discriminate <i>after</i> paying for an agent run is the expensive way round.
/// </para>
/// </remarks>
public sealed class ProceduralIncidentTaskTests
{
    [Fact]
    public void CompletionCannotBeClaimedInProse()
    {
        // Completion is checked against a marker only the real chain emits. An agent that learned a
        // WRONG procedure reports success fluently, and that failure is invisible to every efficiency
        // number the harness collects.
        var task = new ProceduralIncidentTask();

        task.IsComplete("I have restored the service successfully.").Should().BeFalse();
        task.IsComplete("Done — the rollback is complete and traffic is healthy.").Should().BeFalse();
    }

    [Fact]
    public void RepublishingIsRefusedWithoutAChangeWindow()
    {
        // THE undocumented convention, and the only thing worth writing a runbook about. Nothing in
        // acquire_change_window's name or description connects it to republishing, so this is
        // discoverable only by being refused.
        var task = new ProceduralIncidentTask();
        var republish = Tool(task, "RepublishPrevious");

        var result = Invoke(republish, new { service = "checkout-api", quarantine = "QTN-8823" });

        result.Should().StartWith(ProceduralIncidentTask.RefusalPrefix);
        ProceduralBenchmarkTask.IsRefusal(result).Should().BeTrue(
            "promotion must store the calls that WORKED, not the transcript of stumbling into success");
    }

    [Fact]
    public void RepublishingIsRefusedWithoutTheRegistryToken()
    {
        // The non-inferable dependency. The token exists, and only an "artifact registry" lookup --
        // a name suggesting inventory, not authorisation -- yields it.
        var task = new ProceduralIncidentTask();
        Invoke(Tool(task, "AcquireChangeWindow"), new { service = "checkout-api" });

        var result = Invoke(
            Tool(task, "RepublishPrevious"), new { service = "checkout-api", quarantine = "guessed" });

        result.Should().StartWith(ProceduralIncidentTask.RefusalPrefix);
    }

    [Fact]
    public void TheFullChainCompletes()
    {
        // The task must be solvable, or the arms tie at zero and the harness measures nothing.
        var task = new ProceduralIncidentTask();

        var registry = Invoke(Tool(task, "InspectArtifactRegistry"), new { service = "checkout-api" });
        registry.Should().Contain("QTN-8823");
        Invoke(Tool(task, "AcquireChangeWindow"), new { service = "checkout-api" });
        var done = Invoke(
            Tool(task, "RepublishPrevious"), new { service = "checkout-api", quarantine = "QTN-8823" });

        task.IsComplete(done).Should().BeTrue();
        task.Calls.Should().Equal("InspectArtifactRegistry", "AcquireChangeWindow", "RepublishPrevious");
    }

    [Fact]
    public void NoDescriptionGivesAwayTheChain()
    {
        // The first rail run failed on exactly this: the bodies enforced an ordering and the
        // descriptions announced it, so the model ordered the calls correctly cold and the enforcement
        // never fired. A benchmark whose difficulty lives only in code the model never reads is not a
        // benchmark. Same word list, so the two tasks cannot drift apart on strictness.
        var descriptions = new ProceduralIncidentTask().CreateTools()
            .OfType<AIFunction>()
            .Select(tool => tool.Description ?? string.Empty)
            .ToList();

        descriptions.Should().NotBeEmpty();
        foreach (var description in descriptions)
        {
            foreach (var word in ProceduralBenchmarkTask.ChainRevealingWords)
            {
                description.ToLowerInvariant().Should().NotContain(word.ToLowerInvariant(),
                    $"'{description}' would let the model order the calls without discovering them");
            }
        }
    }

    [Fact]
    public void ThereAreEnoughDecoysThatCallingEverythingIsNotFree()
    {
        // The third rail run's finding: with a handful of tools an agent skips discovery entirely by
        // invoking all of them, the unguided policy is already near-optimal, and a stored procedure
        // cannot pay for itself. Exhaustive calling has to cost more than the task is worth.
        var tools = new ProceduralIncidentTask().CreateTools();

        tools.Count.Should().BeGreaterThan(12);
    }

    [Fact]
    public void ItIsStructurallyDifferentFromTheRailTask()
    {
        // A second task that merely renamed the first would measure the same thing twice. The chains
        // differ in length and in where the gate sits.
        var incident = new ProceduralIncidentTask();
        Invoke(Tool(incident, "InspectArtifactRegistry"), new { service = "checkout-api" });
        Invoke(Tool(incident, "AcquireChangeWindow"), new { service = "checkout-api" });
        Invoke(Tool(incident, "RepublishPrevious"),
            new { service = "checkout-api", quarantine = "QTN-8823" });

        incident.Calls.Should().HaveCount(3, "the rail task's correct chain is four calls, not three");
    }

    private static AIFunction Tool(ProceduralIncidentTask task, string name) =>
        task.CreateTools().OfType<AIFunction>()
            .First(tool => tool.Name.Replace("_", string.Empty)
                .Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string Invoke(AIFunction tool, object arguments)
    {
        var json = JsonSerializer.Serialize(arguments);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
        return tool.InvokeAsync(new AIFunctionArguments(parsed)).AsTask().GetAwaiter().GetResult()
            ?.ToString() ?? string.Empty;
    }
}
