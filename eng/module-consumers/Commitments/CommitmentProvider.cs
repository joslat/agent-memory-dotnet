using System.Text;
using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;

namespace AgentMemory.Examples.Commitments;

/// <summary>Explicit user-command teaching; no language model, inference, owner selection or persistence.</summary>
public sealed class CommitmentProvider : IIngestionProvider
{
    private readonly IReadOnlyDictionary<string, AgentMemory.Extensibility.Modules.ModuleProperty> _properties =
        CommitmentsModule.Manifest.Ontology!.Types.Single().Properties;
    public IngestionProviderDescriptor Descriptor { get; } = new() { Id = CommitmentsModule.Id + ":teach" };

    public Task<IReadOnlyList<ModuleProposal>> ProposeAsync(IngestionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        if (request.Messages.Count > 64) throw new FormatException("A teaching turn supports at most 64 source messages.");
        var proposals = new List<ModuleProposal>();
        foreach (var message in request.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.Role != "user" || !message.Text.TrimStart().StartsWith('{')) continue;
            if (Encoding.UTF8.GetByteCount(message.Text) > 16 * 1024) throw new FormatException("Teaching JSON exceeds 16 KiB.");
            using var document = Parse(message.Text); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Any(p => p.Name == "format"
                && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "agentmemory.commitment/v1")) continue;
            StrictObject(root, _properties.Keys.Concat(["format", "key", "about"]));
            var key = Text(root, "key");
            if (!root.TryGetProperty("about", out var about)) throw new FormatException("Teaching requires an about target.");
            StrictObject(about, ["kind", "target"]);
            var kind = Text(about, "kind") switch
            {
                "identity" => LinkTargetKind.Identity,
                "name" => LinkTargetKind.EntityName,
                _ => throw new FormatException("Target kind must be identity or name.")
            };
            var properties = root.EnumerateObject().Where(p => _properties.ContainsKey(p.Name))
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal).AsReadOnly();
            var excerpt = message.Text.TrimStart();
            var excerptLength = Math.Min(excerpt.Length, 500);
            if (char.IsHighSurrogate(excerpt[excerptLength - 1])) excerptLength--;
            proposals.Add(new()
            {
                Type = CommitmentsModule.Type, Key = key, Properties = properties,
                Links = [new() { Relation = CommitmentsModule.About, Target = Text(about, "target"), TargetKind = kind }],
                Source = new() { MessageIds = [message.Id], Excerpt = excerpt[..excerptLength] }
            });
        }
        return Task.FromResult<IReadOnlyList<ModuleProposal>>(proposals.AsReadOnly());
    }

    private static JsonDocument Parse(string text)
    {
        try { return JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException exception) { throw new FormatException("Invalid bounded teaching JSON.", exception); }
    }
    private static void StrictObject(JsonElement value, IEnumerable<string> allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a teaching object.");
        var names = new HashSet<string>(StringComparer.Ordinal); var fields = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!names.Add(field.Name) || !fields.Contains(field.Name)) throw new FormatException("Unknown or duplicate teaching field: " + field.Name);
    }
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var field)
        && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())
            ? field.GetString()! : throw new FormatException("Expected nonempty string: " + name);
}
