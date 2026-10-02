using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentMemory.Decisions;

internal sealed record FixtureDecision(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source_file")] string SourceFile,
    [property: JsonPropertyName("heading")] string Heading,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("statement")] string Statement,
    [property: JsonPropertyName("status")] string Status);

internal sealed record FixtureReplacement(
    [property: JsonPropertyName("later")] string Later,
    [property: JsonPropertyName("earlier")] string Earlier);

internal sealed record FixtureQuestion(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("answer_decision_ids")] IReadOnlyList<string> AnswerDecisionIds);

internal sealed record FixtureChain(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("set")] string Set,
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("replacement")] string Replacement,
    [property: JsonPropertyName("control")] bool Control,
    [property: JsonPropertyName("decisions")] IReadOnlyList<FixtureDecision> Decisions,
    [property: JsonPropertyName("replaces")] IReadOnlyList<FixtureReplacement> Replaces,
    [property: JsonPropertyName("questions")] IReadOnlyList<FixtureQuestion> Questions);

internal sealed record FixtureAbstention(
    [property: JsonPropertyName("set")] string Set,
    [property: JsonPropertyName("text")] string Text);

internal sealed record DecisionFixtureFile(
    [property: JsonPropertyName("chains")] IReadOnlyList<FixtureChain> Chains,
    [property: JsonPropertyName("abstention_questions")] IReadOnlyList<FixtureAbstention> AbstentionQuestions)
{
    internal static DecisionFixtureFile Load(string path) =>
        JsonSerializer.Deserialize<DecisionFixtureFile>(File.ReadAllText(path))
        ?? throw new InvalidDataException($"{path} holds no fixture.");
}

/// <summary>One question to ask, with what grades it.</summary>
internal sealed record DecisionQuestion(
    string Id, string Set, string Kind, string Text, string? Date, string? ChainId,
    IReadOnlySet<string> Answers, IReadOnlySet<string> ReplacedAtQuestionTime);

/// <summary>The fixture's decisions placed on the corpus's chunks, and its questions, ready to ask and grade.</summary>
internal static partial class DecisionLabels
{
    /// <summary>Decision id → the chunk ids that hold it (its file, and a heading that matches).</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Locate(
        DecisionFixtureFile fixture, IReadOnlyList<DecisionChunk> chunks)
    {
        var located = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var chain in fixture.Chains)
        foreach (var decision in chain.Decisions)
        {
            // Stage-2 finding (2026-10-01, before any full run): an ADR is one decision, and an answer citing another
            // section of the right ADR names the right decision. So an ADR decision is the whole document, unless
            // another decision of its chain lives in the same file (an addendum that replaces it): then the section.
            var releaseNote = decision.SourceFile.EndsWith("CHANGELOG.md", StringComparison.OrdinalIgnoreCase);
            var sharesItsFile = chain.Decisions.Any(other => other.Id != decision.Id && SameFile(other.SourceFile, decision.SourceFile));
            var wanted = Normalize(LastSegment(decision.Heading));
            located[decision.Id] = [.. chunks
                .Where(chunk => SameFile(chunk.File, decision.SourceFile))
                .Where(chunk => (!releaseNote && !sharesItsFile) || Normalize(LastSegment(chunk.Heading)) is var have &&
                                (have == wanted || (wanted.Length >= 12 && (have.Contains(wanted, StringComparison.Ordinal) ||
                                                                             wanted.Contains(have, StringComparison.Ordinal)))))
                .Where(chunk => !decision.SourceFile.EndsWith("CHANGELOG.md", StringComparison.OrdinalIgnoreCase) ||
                                ReleaseOf(chunk.Heading) == ReleaseOf(decision.Heading))
                .Select(chunk => chunk.Id)];
        }
        return located;
    }

    /// <summary>Every question, with the decisions that answer it and those already replaced when it is asked.</summary>
    internal static IReadOnlyList<DecisionQuestion> Questions(DecisionFixtureFile fixture)
    {
        var questions = new List<DecisionQuestion>();
        foreach (var chain in fixture.Chains)
        {
            var byId = chain.Decisions.ToDictionary(decision => decision.Id, StringComparer.Ordinal);
            var n = 0;
            foreach (var question in chain.Questions)
            {
                // A decision is replaced at the question's time when its replacement was decided on or before that time
                // (now, for the questions without a date).
                var at = question.Date is { } date ? date : "9999-12-31";
                var replaced = chain.Replaces
                    .Where(link => byId.TryGetValue(link.Later, out var later) && string.CompareOrdinal(later.Date, at) <= 0)
                    .Select(link => link.Earlier)
                    .ToHashSet(StringComparer.Ordinal);
                questions.Add(new DecisionQuestion($"{chain.Id}.q{++n}", chain.Set, question.Kind, question.Text, question.Date,
                    chain.Id, question.AnswerDecisionIds.ToHashSet(StringComparer.Ordinal), replaced));
            }
        }
        var a = 0;
        foreach (var abstention in fixture.AbstentionQuestions)
            questions.Add(new DecisionQuestion($"{abstention.Set}-abs.q{++a}", abstention.Set, "abstention", abstention.Text, null, null,
                new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal)));
        return questions;
    }

    private static bool SameFile(string a, string b) =>
        a.Replace('\\', '/').EndsWith(b.Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase) ||
        b.Replace('\\', '/').EndsWith(a.Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase);

    private static string LastSegment(string heading)
    {
        var at = heading.LastIndexOf(" > ", StringComparison.Ordinal);
        return at < 0 ? heading : heading[(at + 3)..];
    }

    private static string? ReleaseOf(string heading) => ReleaseVersion().Match(heading) is { Success: true } m ? m.Value : null;

    internal static string Normalize(string text) =>
        Spaces().Replace(NotWord().Replace(text.ToLowerInvariant().Replace("`", "", StringComparison.Ordinal), " "), " ").Trim();

    [GeneratedRegex(@"\[\d+\.\d+\.\d+[^\]]*\]")]
    private static partial Regex ReleaseVersion();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NotWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>What one system answered to one question, and how it grades.</summary>
internal sealed record DecisionAnswer(
    string QuestionId, string Kind, bool Abstained, IReadOnlyList<string> CitedChunks,
    IReadOnlyList<string> CitedDecisions, bool Correct, bool Leak, string Answer);

internal static class DecisionGrading
{
    /// <summary>Grades an answer from its citations: no judge model.</summary>
    internal static DecisionAnswer Grade(
        DecisionQuestion question, bool abstained, IReadOnlyList<string> citedChunks, string answer,
        IReadOnlyDictionary<string, IReadOnlyList<string>> chunksOfDecision)
    {
        var cited = chunksOfDecision
            .Where(pair => pair.Value.Intersect(citedChunks, StringComparer.Ordinal).Any())
            .Select(pair => pair.Key)
            .ToList();
        if (question.Kind == "abstention")
            return new DecisionAnswer(question.Id, question.Kind, abstained, citedChunks, cited, abstained, false, answer);

        var correct = !abstained && cited.Any(question.Answers.Contains);
        var leak = !abstained && question.Kind is "in_force_now" or "in_force_on_date" &&
                   cited.Any(id => question.ReplacedAtQuestionTime.Contains(id) && !question.Answers.Contains(id));
        return new DecisionAnswer(question.Id, question.Kind, abstained, citedChunks, cited, correct && !leak, leak, answer);
    }

    /// <summary>The pre-registered metrics over one system's answers.</summary>
    internal static IReadOnlyDictionary<string, double> Summarize(IReadOnlyList<DecisionAnswer> answers)
    {
        double Rate(Func<DecisionAnswer, bool> filter, Func<DecisionAnswer, bool> hit)
        {
            var pool = answers.Where(filter).ToList();
            return pool.Count == 0 ? double.NaN : 100.0 * pool.Count(hit) / pool.Count;
        }
        bool InForce(DecisionAnswer a) => a.Kind is "in_force_now" or "in_force_on_date";
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["in_force_precision"] = Rate(InForce, a => a.Correct),
            ["superseded_leak"] = Rate(InForce, a => a.Leak),
            ["as_of_accuracy"] = Rate(a => a.Kind == "in_force_on_date", a => a.Correct),
            ["lineage_match"] = Rate(a => a.Kind == "what_replaced", a => a.Correct),
            ["source_match"] = Rate(a => a.Kind == "why", a => a.Correct),
            ["abstention"] = Rate(a => a.Kind == "abstention", a => a.Correct),
            ["false_refusal"] = Rate(a => a.Kind != "abstention", a => a.Abstained),
        };
    }
}
