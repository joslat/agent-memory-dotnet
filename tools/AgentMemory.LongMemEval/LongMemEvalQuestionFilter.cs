using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentEval.Memory.External.Models;

namespace AgentMemory.LongMemEval;

/// <summary>
/// <c>--question-ids</c>: narrows a run to named questions from the usual draw.
/// </summary>
/// <remarks>
/// <para>
/// <b>A filter, never a sampler.</b> The ids must all belong to the set the command would have drawn
/// anyway (same count, seed and abstention policy). An id outside the draw fails the run and is named,
/// because it means the command points at a different base set than its author believes. A pilot that
/// quietly ran on questions outside its parent 50 could not be compared with that 50 at all.
/// </para>
/// <para>
/// The kept ids run in <b>draw order</b>, whatever order they were typed in, so the same subset always
/// runs the same way.
/// </para>
/// </remarks>
internal sealed class LongMemEvalQuestionFilter
{
    internal const string Option = "--question-ids";

    private LongMemEvalQuestionFilter(IReadOnlyList<string> requestedIds, string specification)
    {
        RequestedIds = requestedIds;
        Specification = specification;
    }

    /// <summary>The ids as given, in the order given.</summary>
    internal IReadOnlyList<string> RequestedIds { get; }

    /// <summary><c>inline</c>, or <c>@</c> followed by the full path of the id file.</summary>
    internal string Specification { get; }

    /// <summary>
    /// Parses <c>--question-ids a,b,c</c> or <c>--question-ids @ids.txt</c>, or returns null when absent.
    /// </summary>
    /// <remarks>
    /// A file takes one id per line or comma-separated ids. Blank lines and lines starting with <c>#</c>
    /// are ignored. Duplicates and an empty list are refused rather than tidied: either is a typing
    /// mistake, and silently repairing one hides what was actually asked for.
    /// </remarks>
    internal static LongMemEvalQuestionFilter? Parse(string? value)
    {
        if (value is null) return null;

        string text;
        string specification;
        if (value.StartsWith('@'))
        {
            var path = Path.GetFullPath(value[1..]);
            if (!File.Exists(path))
                throw new ArgumentException($"{Option} file not found: {path}");
            text = File.ReadAllText(path);
            specification = "@" + path;
        }
        else
        {
            text = value;
            specification = "inline";
        }

        var ids = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .SelectMany(line => line.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();

        if (ids.Length == 0)
            throw new ArgumentException($"{Option} names no question.");

        var duplicates = ids
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
            throw new ArgumentException(
                $"{Option} names {string.Join(", ", duplicates)} more than once.");

        return new LongMemEvalQuestionFilter(ids, specification);
    }

    /// <summary>
    /// The requested ids in draw order. Throws, naming them, when any lies outside the draw.
    /// </summary>
    internal IReadOnlyList<string> SelectFrom(IReadOnlyList<string> drawnIds, string drawDescription)
    {
        ArgumentNullException.ThrowIfNull(drawnIds);

        var drawn = drawnIds.ToHashSet(StringComparer.Ordinal);
        var missing = RequestedIds.Where(id => !drawn.Contains(id)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"{Option} names {missing.Length} question(s) outside the drawn set ({drawDescription}): "
                + $"{string.Join(", ", missing)}. The filter only narrows the usual draw, so an id outside "
                + "it means this command points at a different base set.");
        }

        var requested = RequestedIds.ToHashSet(StringComparer.Ordinal);
        return drawnIds.Where(requested.Contains).ToArray();
    }
}

/// <summary>
/// The questions a run evaluates, and the dataset file and AgentEval options that produce them.
/// </summary>
/// <param name="Options">What the runner and the evidence index are built with.</param>
/// <param name="DatasetPath">The source dataset, or the derived one when a filter is applied.</param>
/// <param name="QuestionCount">The number of questions the run evaluates.</param>
/// <param name="EvidenceIndex">The index already built for a filtered run; null otherwise.</param>
/// <param name="Record">The report's record of the filter; null when there is none.</param>
internal sealed record LongMemEvalQuestionSelection(
    ExternalBenchmarkOptions Options,
    string DatasetPath,
    int QuestionCount,
    LongMemEvalEvidenceIndex? EvidenceIndex,
    LongMemEvalQuestionFilterRecord? Record)
{
    /// <summary>Where derived datasets are written: gitignored, and named by their own content.</summary>
    internal static string DefaultDirectory => Path.Combine("artifacts", "evaluation", "question-filters");

    /// <summary>
    /// Applies <paramref name="filter"/> after the usual draw, or passes the draw through untouched.
    /// </summary>
    /// <param name="sourceDatasetPath">The dataset the draw is taken from.</param>
    /// <param name="drawnOptions">The options of the usual draw.</param>
    /// <param name="filter">The filter, or null for none.</param>
    /// <param name="optionsForDataset">
    /// Builds the options for a dataset path and question count, with everything else as the verb set it.
    /// </param>
    /// <param name="directory">Where to write the derived dataset.</param>
    /// <remarks>
    /// <para>
    /// <b>Why a derived file.</b> AgentEval's runner draws its own questions from the file it is given
    /// and has no id filter. The kept entries are therefore copied, byte for byte, into a file that holds
    /// exactly them. AgentEval returns every entry of such a file in file order, and the file is written in
    /// draw order.
    /// </para>
    /// <para>
    /// That is checked rather than assumed. The derived draw must return the same ids in the same order,
    /// and every question must format to the same history as it did in the original draw. Any difference
    /// fails the run: a history that formats differently would be a different question under the same id.
    /// </para>
    /// <para>
    /// Without a filter nothing changes. The options, the dataset path and the moment the evidence index
    /// is loaded are all exactly as before.
    /// </para>
    /// </remarks>
    internal static LongMemEvalQuestionSelection Resolve(
        string sourceDatasetPath,
        ExternalBenchmarkOptions drawnOptions,
        LongMemEvalQuestionFilter? filter,
        Func<string, int, ExternalBenchmarkOptions> optionsForDataset,
        string? directory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatasetPath);
        ArgumentNullException.ThrowIfNull(drawnOptions);
        ArgumentNullException.ThrowIfNull(optionsForDataset);

        if (filter is null)
        {
            return new LongMemEvalQuestionSelection(
                drawnOptions,
                sourceDatasetPath,
                drawnOptions.MaxQuestions ?? 0,
                EvidenceIndex: null,
                Record: null);
        }

        var drawn = LongMemEvalEvidenceIndex.Load(sourceDatasetPath, drawnOptions);
        var drawnIds = drawn.Questions.Select(question => question.QuestionId).ToArray();
        var selected = filter.SelectFrom(drawnIds, Describe(drawnOptions, drawnIds.Length));

        var (derivedPath, derivedSha256) = LongMemEvalFilteredDataset.Write(
            sourceDatasetPath, selected, directory ?? DefaultDirectory);
        var options = optionsForDataset(derivedPath, selected.Count);
        var index = LongMemEvalEvidenceIndex.Load(derivedPath, options);

        var loadedIds = index.Questions.Select(question => question.QuestionId).ToArray();
        if (!loadedIds.SequenceEqual(selected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"The derived dataset for {LongMemEvalQuestionFilter.Option} drew "
                + $"[{string.Join(", ", loadedIds)}], not the selected [{string.Join(", ", selected)}].");
        }

        var drawnById = drawn.Questions.ToDictionary(question => question.QuestionId, StringComparer.Ordinal);
        foreach (var question in index.Questions)
        {
            var original = LongMemEvalEvidenceIndex.Fingerprint(
                LongMemEvalBenchmarkProtocol.History(drawnById[question.QuestionId]));
            var derived = LongMemEvalEvidenceIndex.Fingerprint(LongMemEvalBenchmarkProtocol.History(question));
            if (!string.Equals(original, derived, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Question {question.QuestionId} formats differently from the derived dataset than from "
                    + "the original draw; the filtered run would not be the same question.");
            }
        }

        return new LongMemEvalQuestionSelection(
            options,
            derivedPath,
            selected.Count,
            index,
            new LongMemEvalQuestionFilterRecord(
                selected,
                selected.Count,
                filter.Specification,
                drawnIds.Length,
                Describe(drawnOptions, drawnIds.Length),
                derivedPath,
                derivedSha256));
    }

    private static string Describe(ExternalBenchmarkOptions options, int drawn) =>
        $"{drawn} drawn with seed {options.RandomSeed?.ToString() ?? "none"}, abstention "
        + $"{options.AbstentionPolicy}"
        + (options.AbstentionTargetProportion is { } proportion
            ? $" {proportion.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : string.Empty)
        + (options.IncludeQuestionTypes is { Count: > 0 } types ? $", types {string.Join("+", types)}" : string.Empty);
}

/// <summary>What the report records about a question filter.</summary>
/// <param name="QuestionIds">The questions evaluated, in draw order.</param>
/// <param name="QuestionCount">How many.</param>
/// <param name="Specification"><c>inline</c> or <c>@path</c>.</param>
/// <param name="DrawnQuestions">The size of the draw the ids were taken from.</param>
/// <param name="Draw">How that draw was made.</param>
/// <param name="DerivedDataset">The file the run actually read.</param>
/// <param name="DerivedDatasetSha256">Its sha256. The source dataset's sha stays in <c>datasetSha256</c>.</param>
internal sealed record LongMemEvalQuestionFilterRecord(
    IReadOnlyList<string> QuestionIds,
    int QuestionCount,
    string Specification,
    int DrawnQuestions,
    string Draw,
    string DerivedDataset,
    string DerivedDatasetSha256);

/// <summary>Writes a LongMemEval dataset holding only the named entries of another.</summary>
internal static class LongMemEvalFilteredDataset
{
    /// <summary>
    /// Copies the named entries, byte for byte and in the order given, into a new JSON array.
    /// </summary>
    /// <returns>The written file's full path and its sha256. The file is named after that sha.</returns>
    /// <remarks>
    /// Each entry is copied from its raw source text rather than re-serialised, so no escaping or number
    /// formatting can change what AgentEval reads. The same inputs always produce the same file.
    /// </remarks>
    internal static (string Path, string Sha256) Write(
        string sourcePath, IReadOnlyList<string> orderedIds, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(orderedIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var wanted = orderedIds.ToHashSet(StringComparer.Ordinal);
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var stream = File.OpenRead(sourcePath))
        using (var document = JsonDocument.Parse(stream))
        {
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("question_id", out var id) &&
                    id.GetString() is { } value &&
                    wanted.Contains(value))
                {
                    raw[value] = entry.GetRawText();
                }
            }
        }

        var absent = orderedIds.Where(id => !raw.ContainsKey(id)).ToArray();
        if (absent.Length > 0)
            throw new InvalidOperationException(
                $"The dataset holds no entry for: {string.Join(", ", absent)}.");

        var bytes = Encoding.UTF8.GetBytes("[" + string.Join(",", orderedIds.Select(id => raw[id])) + "]");
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, $"longmemeval-filtered-{sha256[..16]}.json"));
        File.WriteAllBytes(path, bytes);
        return (path, sha256);
    }
}
