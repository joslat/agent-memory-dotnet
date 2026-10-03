namespace AgentMemory.Validation;

/// <summary>What one run of a pack found.</summary>
public sealed record PackRunResult
{
    /// <summary>The pack's id.</summary>
    public required string PackId { get; init; }

    /// <summary>The pack's title.</summary>
    public required string Title { get; init; }

    /// <summary>The prefix this run's owners and sessions were written under, so runs never meet in one store.</summary>
    public required string RunPrefix { get; init; }

    /// <summary>Every check, in the order it ran.</summary>
    public IReadOnlyList<PackCheckResult> Checks { get; init; } = [];

    /// <summary>For each question: the kinds the pack says hold its answer, and where the expected items were found.</summary>
    public IReadOnlyList<PackRouteRecord> Routes { get; init; } = [];

    /// <summary>True when every check passed.</summary>
    public bool Passed => Checks.Count > 0 && Checks.All(c => c.Passed);

    /// <summary>The checks that failed.</summary>
    public IEnumerable<PackCheckResult> Failures => Checks.Where(c => !c.Passed);
}

/// <summary>
/// One check: <c>pack</c> (the pack's own data or options), <c>storage</c>, <c>recall</c> (an expected or excluded item)
/// or <c>isolation</c> (another owner's item in a recall).
/// </summary>
/// <param name="Id">Stable within the pack: <c>storage:2</c>, <c>recall:q1:expect:1</c>, <c>isolation:q1</c>.</param>
/// <param name="Kind">pack, storage, recall or isolation.</param>
/// <param name="Passed">Whether it held.</param>
/// <param name="Detail">What was checked and, when it failed, what was found instead.</param>
public sealed record PackCheckResult(string Id, string Kind, bool Passed, string Detail);

/// <summary>A question's route: the gold kinds and where its expected items were found (the routing set's raw data).</summary>
/// <param name="QuestionId">The question.</param>
/// <param name="Gold">The kinds the pack says hold the answer.</param>
/// <param name="FoundIn">The kinds the expected items were recalled from.</param>
public sealed record PackRouteRecord(string QuestionId, IReadOnlyList<string> Gold, IReadOnlyList<string> FoundIn);
