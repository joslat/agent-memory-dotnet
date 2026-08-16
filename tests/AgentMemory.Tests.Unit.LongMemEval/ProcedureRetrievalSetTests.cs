using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 26.2. The <c>LME_Procedural</c> retrieval set has to be a valid measuring stick before any number
/// taken with it means anything.
/// </summary>
/// <remarks>
/// <para>
/// A labelled set can be silently broken in ways that make a retriever look good: a label pointing at
/// a procedure that was never stored is an unwinnable query, and a set with no abstain cases cannot
/// distinguish a cautious retriever from a reckless one. Both would still produce a confident-looking
/// percentage.
/// </para>
/// <para>
/// Provider-free and deterministic — this is the dataset, not the retrieval.
/// </para>
/// </remarks>
public sealed class ProcedureRetrievalSetTests
{
    [Fact]
    public void EveryProcedureIdIsUnique()
    {
        // Duplicate ids would make scoring ambiguous: a retrieval "hit" could match two labels.
        ProcedureRetrievalSet.Procedures.Select(procedure => procedure.Id)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryQueryIdIsUnique()
    {
        ProcedureRetrievalSet.Queries.Select(query => query.TaskId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryLabelPointsAtAProcedureThatIsActuallyStored()
    {
        // THE load-bearing check. A label naming a procedure absent from the fixture list makes that
        // query unwinnable by construction, and it would read as a retrieval failure forever.
        var stored = ProcedureRetrievalSet.Procedures.Select(procedure => procedure.Id).ToHashSet(StringComparer.Ordinal);

        var dangling = ProcedureRetrievalSet.Queries
            .SelectMany(query => query.Correct)
            .Where(id => !stored.Contains(id))
            .ToList();

        dangling.Should().BeEmpty("a label pointing at an unstored procedure is an unwinnable query");
    }

    [Fact]
    public void AMeaningfulShareOfQueriesShouldAbstain()
    {
        // Without abstain cases, WrongProcedureRate is unmeasurable: a retriever that always answers
        // scores identically to one that knows when to stay quiet, and abstention is the safe outcome
        // this instrument exists to keep visible.
        var abstain = ProcedureRetrievalSet.Queries.Count(query => query.Correct.Count == 0);

        abstain.Should().BeGreaterThan(0);
        ((double)abstain / ProcedureRetrievalSet.Queries.Count).Should().BeGreaterThan(0.15,
            "too few abstain cases and the metric cannot separate caution from recklessness");
    }

    [Fact]
    public void EveryProcedureRecordsAnOrderingRatherThanARestatement()
    {
        // The Outcome is the procedure. A trace whose outcome merely repeats its task tells an agent
        // it has done this before and nothing about how — the exact product gap that made procedural
        // memory retrievable and mute until 2026-08-13.
        foreach (var procedure in ProcedureRetrievalSet.Procedures)
        {
            procedure.Outcome.Should().NotBeNullOrWhiteSpace();
            procedure.Outcome.Should().NotBe(procedure.Task);
            procedure.Outcome.Should().Contain(" then ",
                $"'{procedure.Id}' must record a step ordering, not a description");
        }
    }

    [Fact]
    public void TheSetContainsNearMissesRatherThanOnlyObviousDistinctions()
    {
        // A set where every wrong answer is obviously wrong measures nothing. Several procedures must
        // share a task family, so surface similarity alone picks the wrong sibling.
        var families = ProcedureRetrievalSet.Procedures
            .GroupBy(procedure => procedure.Id.Split('-')[^1], StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToList();

        families.Should().NotBeEmpty(
            "the set needs procedures that compete with each other, or retrieval is trivial");
    }

    [Fact]
    public void AnswerableQueriesDoNotSimplyRestateTheirProcedureTask()
    {
        // If every answerable query were the stored task verbatim, this would measure string equality
        // wearing an embedding. At least half must be genuine paraphrases.
        var tasks = ProcedureRetrievalSet.Procedures
            .ToDictionary(procedure => procedure.Id, procedure => procedure.Task, StringComparer.Ordinal);

        var answerable = ProcedureRetrievalSet.Queries.Where(query => query.Correct.Count > 0).ToList();
        var verbatim = answerable.Count(query =>
            query.Correct.Any(id => string.Equals(tasks[id], query.Query, StringComparison.OrdinalIgnoreCase)));

        verbatim.Should().BeLessThan(answerable.Count / 2,
            "a set of verbatim restatements measures string matching, not retrieval");
    }
}
