using System.Reflection;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Validation;

/// <summary>The store's clock during a pack run: moved to each message's and question's time (V4).</summary>
internal sealed class ReplayClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset Now { get; set; } = start;

    public DateTimeOffset UtcNow => Now;
}

/// <summary>What each message yields, keyed by message id: the pack plays the model (V1).</summary>
internal sealed class PackScript
{
    private readonly Dictionary<string, PackMessage> _byMessage = new(StringComparer.Ordinal);

    public int Count => _byMessage.Count;

    public void Add(string messageId, PackMessage message) => _byMessage[messageId] = message;

    public IEnumerable<PackMessage> For(IEnumerable<Message> messages) =>
        messages.Select(m => _byMessage.GetValueOrDefault(m.MessageId)).OfType<PackMessage>();

    public static DatePrecision Precision(string? precision) => precision?.ToLowerInvariant() switch
    {
        "day" => DatePrecision.Day,
        "month" => DatePrecision.Month,
        "year" => DatePrecision.Year,
        _ => DatePrecision.Unspecified,
    };
}

internal sealed class ScriptedEntities(PackScript script) : IEntityExtractor
{
    public Task<IReadOnlyList<ExtractedEntity>> ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ExtractedEntity>>([.. script.For(messages).SelectMany(m => m.Entities).Select(e =>
            new ExtractedEntity { Name = e.Name, Type = e.Type, Aliases = e.Aliases, Confidence = 0.95 })]);
}

internal sealed class ScriptedFacts(PackScript script) : IFactExtractor
{
    public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ExtractedFact>>([.. script.For(messages).SelectMany(m => m.Facts.Select(f => new ExtractedFact
        {
            Subject = f.Subject,
            Predicate = f.Predicate,
            Object = f.Object,
            Confidence = 0.95,
            ValidFrom = f.ValidFrom,
            ValidFromPrecision = f.ValidFrom is null ? DatePrecision.Unspecified : PackScript.Precision(f.Precision),
            ValidUntil = f.ValidUntil,
            ValidUntilPrecision = f.ValidUntil is null ? DatePrecision.Unspecified : PackScript.Precision(f.Precision),
            OccurredOn = f.OccurredOn,
            OccurredOnPrecision = f.OccurredOn is null ? DatePrecision.Unspecified : PackScript.Precision(f.Precision),
            Replaces = f.Replaces,
            SourceRole = m.Role,
        }))]);
}

internal sealed class ScriptedRelationships(PackScript script) : IRelationshipExtractor
{
    public Task<IReadOnlyList<ExtractedRelationship>> ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ExtractedRelationship>>([.. script.For(messages).SelectMany(m => m.Relationships).Select(r =>
            new ExtractedRelationship { SourceEntity = r.Source, TargetEntity = r.Target, RelationshipType = r.Type, Confidence = 0.95 })]);
}

internal sealed class ScriptedPreferences(PackScript script) : IPreferenceExtractor
{
    public Task<IReadOnlyList<ExtractedPreference>> ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ExtractedPreference>>([.. script.For(messages).SelectMany(m => m.Preferences.Select(p =>
            new ExtractedPreference { Category = p.Category, PreferenceText = p.Text, Replaces = p.Replaces, Confidence = 0.95, SourceRole = m.Role }))]);
}

/// <summary>Applies a pack's switches (V6): <c>"Extraction.BitemporalChanges": true</c> on <c>MemoryOptions</c>.</summary>
internal static class OptionPaths
{
    /// <summary>Sets <paramref name="path"/> on <paramref name="root"/>; the reason it cannot, or null when it did.</summary>
    public static string? Apply(object root, string path, JsonElement value)
    {
        var parts = path.Split('.');
        var target = root;
        foreach (var part in parts[..^1])
        {
            var step = target.GetType().GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
            if (step?.GetValue(target) is not { } next) return $"'{path}': no settable options at '{part}'";
            target = next;
        }
        var property = target.GetType().GetProperty(parts[^1], BindingFlags.Public | BindingFlags.Instance);
        // Init-only properties are refused: that is how the shared instances are built (RecallOptions.Default is one
        // object for the process), and setting one by reflection would change every host in it.
        if (property?.SetMethod is not { IsPublic: true } setter ||
            setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit"))
            return $"'{path}': not a settable option";
        try
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? converted = value.ValueKind == JsonValueKind.Null ? null
                : type == typeof(bool) ? value.GetBoolean()
                : type == typeof(int) ? value.GetInt32()
                : type == typeof(double) ? value.GetDouble()
                : type == typeof(string) ? value.GetString()
                : type.IsEnum ? Enum.Parse(type, value.GetString()!, ignoreCase: true)
                : throw new NotSupportedException($"options of type {type.Name}");
            property.SetValue(target, converted);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or NotSupportedException)
        {
            return $"'{path}': {ex.Message}";
        }
    }
}
