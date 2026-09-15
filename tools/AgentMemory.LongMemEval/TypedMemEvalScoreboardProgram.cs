namespace AgentMemory.LongMemEval;

/// <summary>
/// <c>--scoreboard</c>: assembles the ten-vertical table from stored artifacts.
/// </summary>
/// <remarks>
/// Reads artifacts and nothing else — no store, no agent, no judge, no spend — so it can be run at
/// any point in a chain, including while a vertical is still in flight. A vertical that has not
/// landed reads ABSENT, which is the whole reason it is safe to run early.
/// </remarks>
internal static class TypedMemEvalScoreboardProgram
{
    private const string DefaultArtifacts = "artifacts/evaluation";

    internal static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var directory = Value(args, "--artifacts") ?? DefaultArtifacts;
        var arm = Value(args, "--arm") ?? "default";

        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"scoreboard: artifacts directory not found: {directory}");
            return 1;
        }

        // No release argument: lineage is settled per row against the corpus THIS BUILD carries, so
        // the table verifies itself against the package in hand rather than against a typed pin.
        var scoreboard = TypedMemEvalScoreboard.Assemble(directory, arm);
        TypedMemEvalScoreboard.Print(scoreboard, Console.Out);

        // A refusal is a failure exit: the caller is usually a chain script, and a scoreboard that
        // could not legitimately be assembled must not scroll past as though it had been.
        return scoreboard.Refusals.Count > 0 ? 2 : 0;
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
