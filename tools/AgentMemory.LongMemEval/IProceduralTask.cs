using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// The seam that lets the benefit harness run more than one task shape (26.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>This interface exists because its absence produced the defect it now prevents.</b>
/// <c>ProceduralIncidentTask</c> was written, given seven validity tests, and committed with a message
/// claiming the sample had gone from one task to two — while
/// <c>ProceduralBenefitProgram</c> still constructed <c>ProceduralBenchmarkTask</c> in three hardcoded
/// places. The task was complete, tested and reachable by nothing: the fifteenth instance of that
/// shape in this track, and the first one committed by the person cataloguing them.
/// </para>
/// <para>
/// A harness that names one concrete task in three places cannot grow a second, and "add a task" being
/// a three-site edit is what makes it tempting to skip the wiring and trust the tests.
/// </para>
/// </remarks>
internal interface IProceduralTask
{
    /// <summary>What the agent is asked to do.</summary>
    string Prompt { get; }

    /// <summary>Whether a response proves the real chain ran, rather than claiming it did.</summary>
    bool IsComplete(string response);

    /// <summary>The tools, including the decoys that make exhaustive calling expensive.</summary>
    IReadOnlyList<AITool> CreateTools();

    /// <summary>Recorded calls, so a test can assert the chain without a model.</summary>
    List<string> Calls { get; }
}

/// <summary>Selects a task shape by name, so the harness never names a concrete one.</summary>
internal static class ProceduralTasks
{
    /// <summary>Every shape the benefit harness can run.</summary>
    /// <remarks>
    /// Reflected over by a test, so a task added here without being runnable — or runnable without
    /// being listed — fails rather than sits unreachable.
    /// </remarks>
    internal static IReadOnlyList<string> Names { get; } = ["rail", "incident"];

    internal static IProceduralTask Create(string name) => name.ToLowerInvariant() switch
    {
        "rail" => new ProceduralBenchmarkTask(),
        "incident" => new ProceduralIncidentTask(),
        _ => throw new ArgumentException(
            $"--task must be one of: {string.Join(", ", Names)}; got '{name}'."),
    };

    /// <summary>
    /// Whether a tool result is a refusal. Shared, because both environments use the same prefix and
    /// promotion must store the calls that <i>worked</i> in either of them.
    /// </summary>
    internal static bool IsRefusal(string result) => ProceduralBenchmarkTask.IsRefusal(result);
}
