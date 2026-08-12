using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Maps LongMemEval's <b>task</b> labels onto <b>memory types</b>.
/// </summary>
/// <remarks>
/// <para>
/// The dataset labels a question by what it asks for; a memory type describes which retrieval key
/// answers it. <b>A task taxonomy is not a memory-type taxonomy</b>, and treating one as the other
/// silently is how a per-type table ends up measuring something other than what it claims.
/// </para>
/// <para>
/// The mapping is embedded <b>data</b>, not code: it is an opinion, it will be revised, and a reader
/// disagreeing with it should be able to see and change it without touching the harness. Every
/// per-type figure inherits its choices, so <see cref="Revision"/> travels with any output.
/// </para>
/// </remarks>
internal sealed class LongMemEvalMemoryTypeMap
{
    private const string ResourceName = "AgentMemory.LongMemEval.Taxonomy.memory-type-map.json";

    /// <summary>The metamemory type, reached only through abstention questions.</summary>
    internal const string MetaMemory = "metamemory";

    /// <summary>Reported for a task label the mapping does not cover.</summary>
    internal const string Unmapped = "unmapped";

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _byTaskType;
    private readonly IReadOnlyList<string> _abstentionTypes;

    /// <summary>The mapping revision, so a per-type figure can name the opinion it came from.</summary>
    internal string Revision { get; }

    private LongMemEvalMemoryTypeMap(
        string revision,
        IReadOnlyDictionary<string, IReadOnlyList<string>> byTaskType,
        IReadOnlyList<string> abstentionTypes)
    {
        Revision = revision;
        _byTaskType = byTaskType;
        _abstentionTypes = abstentionTypes;
    }

    /// <summary>The embedded mapping.</summary>
    internal static LongMemEvalMemoryTypeMap Default { get; } = Load();

    private static LongMemEvalMemoryTypeMap Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded memory-type map '{ResourceName}' is missing. Every per-type figure depends "
                + "on it, so an absent mapping fails loudly rather than defaulting to an invented one.");

        var document = JsonSerializer.Deserialize<MapDocument>(stream, SerializerOptions)
            ?? throw new InvalidOperationException("Embedded memory-type map could not be deserialized.");

        var byTask = document.TaskTypes.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.MemoryTypes,
            StringComparer.OrdinalIgnoreCase);

        return new LongMemEvalMemoryTypeMap(
            document.Revision,
            byTask,
            document.Abstention?.MemoryTypes ?? [MetaMemory]);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// The memory types a question exercises.
    /// </summary>
    /// <remarks>
    /// <paramref name="isAbstention"/> takes precedence: an <c>_abs</c> question is answerable only by
    /// knowing memory does <i>not</i> hold the answer, whatever its task label says it is about. An
    /// unrecognised label maps to <see cref="Unmapped"/> rather than being dropped — a shrinking
    /// denominator is how a metric stops adding up.
    /// </remarks>
    internal IReadOnlyList<string> ForQuestion(string? taskType, bool isAbstention)
    {
        if (isAbstention) return _abstentionTypes;
        if (taskType is not null && _byTaskType.TryGetValue(taskType, out var types)) return types;
        return [Unmapped];
    }

    private sealed class MapDocument
    {
        [JsonPropertyName("revision")] public string Revision { get; set; } = "unknown";
        [JsonPropertyName("taskTypes")] public Dictionary<string, TaskTypeEntry> TaskTypes { get; set; } = [];
        [JsonPropertyName("abstention")] public TaskTypeEntry? Abstention { get; set; }
    }

    private sealed class TaskTypeEntry
    {
        [JsonPropertyName("memoryTypes")] public List<string> MemoryTypes { get; set; } = [];
    }
}
