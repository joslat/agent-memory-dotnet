using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Extensibility.Context;

namespace AgentMemory.Extensibility.Modules;

/// <summary>
/// One task of a retrieval trial (feature 12, R-TRIAL-054): a recorded question, when it was asked, its anchors, and the record
/// keys later used in answering it. A task whose expected keys are none is an abstention task: the pack should offer nothing.
/// </summary>
[Experimental("AMEXT001")]
public sealed record RetrievalTask
{
    /// <summary>The question, as asked.</summary>
    public required string Question { get; init; }
    /// <summary>When it was asked: the world time the trial reads the pack at (feature 07).</summary>
    public required DateTimeOffset At { get; init; }
    /// <summary>Its path or symbol anchors, when it had any.</summary>
    public IReadOnlyList<ContextAnchor> Anchors { get; init; } = [];
    /// <summary>The keys of the records later used, cited or accepted in answering it; none for an abstention task.</summary>
    public IReadOnlyList<string> Expected { get; init; } = [];
    /// <summary>What it tests, when the host says: fact, update, temporal, multi-session or abstain.</summary>
    public string? Ability { get; init; }

    /// <summary>Whether the pack should offer nothing for it.</summary>
    public bool Abstains => Expected.Count == 0;
}

/// <summary>
/// A host's retrieval tasks (feature 12): <c>agentmemory.trial.tasks/1</c>, kept on the host and never in a pack. A decision names
/// it by <see cref="Digest"/>, so it says what it ran on without revealing it.
/// </summary>
[Experimental("AMEXT001")]
public sealed record RetrievalTaskFile(IReadOnlyList<RetrievalTask> Tasks, string Digest)
{
    /// <summary>The format the file names.</summary>
    public const string Format = "agentmemory.trial.tasks/1";
    /// <summary>The abilities a task may name.</summary>
    public static IReadOnlySet<string> Abilities { get; } = new HashSet<string>(["fact", "update", "temporal", "multi-session", "abstain"], StringComparer.Ordinal);
    private const int MaximumTasks = 10_000;

    /// <summary>Reads a task file strictly.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a task file; the message says where.</exception>
    public static RetrievalTaskFile Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException e) { throw new InvalidDataException("The task file is not JSON: " + e.Message); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not ("format" or "tasks"))
                || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
                throw new InvalidDataException($"A task file holds exactly format ('{Format}') and tasks.");
            if (!root.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array || tasks.GetArrayLength() is 0 or > MaximumTasks)
                throw new InvalidDataException($"A task file holds 1 to {MaximumTasks} tasks.");
            var read = new List<RetrievalTask>();
            foreach (var t in tasks.EnumerateArray()) read.Add(Task(t, read.Count + 1));
            return new RetrievalTaskFile(read, "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }

    private static RetrievalTask Task(JsonElement t, int number)
    {
        InvalidDataException Bad(string what) => new($"Task {number}: {what}.");
        if (t.ValueKind != JsonValueKind.Object || t.EnumerateObject().Any(p => p.Name is not ("question" or "at" or "anchors" or "expected" or "ability")))
            throw Bad("a task holds question, at, expected, and optionally anchors and ability");
        if (!t.TryGetProperty("question", out var q) || q.ValueKind != JsonValueKind.String || q.GetString() is not { Length: > 0 and <= 4000 } question)
            throw Bad("its question is 1 to 4,000 characters");
        if (!t.TryGetProperty("at", out var a) || a.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParseExact(a.GetString(), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            throw Bad("its at is an instant in UTC to the second");
        if (!t.TryGetProperty("expected", out var e) || e.ValueKind != JsonValueKind.Array || e.GetArrayLength() > 256
            || e.EnumerateArray().Any(k => k.ValueKind != JsonValueKind.String || k.GetString() is not { Length: > 0 and <= 512 })
            || e.EnumerateArray().Select(k => k.GetString()).Distinct(StringComparer.Ordinal).Count() != e.GetArrayLength())
            throw Bad("its expected keys are 0 to 256 distinct keys");
        var anchors = new List<ContextAnchor>();
        if (t.TryGetProperty("anchors", out var given))
        {
            if (given.ValueKind != JsonValueKind.Array || given.GetArrayLength() > ContextAnchor.MaximumPerRequest) throw Bad($"its anchors are at most {ContextAnchor.MaximumPerRequest}");
            foreach (var anchor in given.EnumerateArray())
            {
                if (anchor.ValueKind != JsonValueKind.Object || anchor.EnumerateObject().Any(p => p.Name is not ("kind" or "value"))
                    || !anchor.TryGetProperty("kind", out var kind) || kind.GetString() is not ("path" or "symbol")
                    || !anchor.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String
                    || value.GetString() is not { Length: > 0 and <= ContextAnchor.MaximumLength } text)
                    throw Bad("an anchor is a kind (path or symbol) and a value");
                anchors.Add(kind.GetString() == "path" ? ContextAnchor.Path(text) : ContextAnchor.Symbol(text));
            }
        }
        string? ability = null;
        if (t.TryGetProperty("ability", out var named))
        {
            if (named.ValueKind != JsonValueKind.String || !Abilities.Contains(named.GetString()!)) throw Bad("its ability is fact, update, temporal, multi-session or abstain");
            ability = named.GetString();
        }
        var expected = e.EnumerateArray().Select(k => k.GetString()!).ToList();
        // An abstention task expects nothing, and one that expects nothing is one.
        if ((ability == "abstain") != (expected.Count == 0)) throw Bad("an abstain task, and only one, expects no keys");
        return new RetrievalTask { Question = question, At = at, Anchors = anchors, Expected = expected, Ability = ability };
    }
}

/// <summary>Where a host's recorded questions come from (feature 12): the questions, their times and the keys later used.</summary>
[Experimental("AMEXT001")]
public interface IQuestionSource
{
    /// <summary>The recorded questions, oldest first, each labelled with the keys the host's labeller says were used.</summary>
    IAsyncEnumerable<RetrievalTask> QuestionsAsync(CancellationToken cancellationToken = default);
}

/// <summary>A question source over a task file the host keeps (feature 12).</summary>
[Experimental("AMEXT001")]
public sealed class TaskFileQuestionSource(string path) : IQuestionSource
{
    /// <inheritdoc/>
    public async IAsyncEnumerable<RetrievalTask> QuestionsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var file = RetrievalTaskFile.Read(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        foreach (var task in file.Tasks.OrderBy(t => t.At)) yield return task;
    }
}
