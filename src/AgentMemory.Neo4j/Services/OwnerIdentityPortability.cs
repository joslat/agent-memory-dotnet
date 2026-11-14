using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Abstractions.Domain;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Services;

/// <summary>
/// ILC-30: how identity records cross from one owner to another. Values keep their stored type; references are rewritten
/// by exact value (record, entity and fact ids, observation keys, the owner's scope) and the owner only where a property
/// names the owner, so a person who happens to share the owner's name is never renamed.
/// </summary>
internal static class OwnerIdentityPortability
{
    internal const string ObservationPrefix = "identity-observation-";
    internal const string DecisionPrefix = "identity-decision-";
    internal const string ImportedMessagePrefix = "imported-message-";

    /// <summary>
    /// The imported observation's key: the same 64-hex shape as a key the ingestion makes, reproducible within one import
    /// and different for each owner and each import, so two copies (two owners, or the same file imported twice) never
    /// collide on (owner_scope, observation key) or on ids (PR260-F5).
    /// </summary>
    internal static string NewObservationKey(string newOwnerId, string importId, string oldKey) => Hash(newOwnerId, importId, oldKey);

    /// <summary>
    /// The importing owner's own marker for a source message the export does not carry (PR260-F8): a copy's decisions,
    /// facts and bindings keep pointing at one another, and never at the original owner's messages, which may live in the
    /// same store. One per owner, not per import: facts are matched by content, so two imports of one file into one owner
    /// share their facts, and both copies' decisions must still find their sources on them.
    /// </summary>
    internal static string NewMessageId(string newOwnerId, string oldMessageId) => ImportedMessagePrefix + Hash(newOwnerId, oldMessageId);

    private static string Hash(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts)))).ToLowerInvariant();

    internal static OwnerGraphValue ToValue(object? value) => value switch
    {
        string text => new() { Kind = OwnerGraphValue.String, Text = text },
        long number => new() { Kind = OwnerGraphValue.Integer, Long = number },
        int number => new() { Kind = OwnerGraphValue.Integer, Long = number },
        double number => new() { Kind = OwnerGraphValue.Number, Double = number },
        float number => new() { Kind = OwnerGraphValue.Number, Double = number },
        bool flag => new() { Kind = OwnerGraphValue.Boolean, Flag = flag },
        ZonedDateTime zoned => new() { Kind = OwnerGraphValue.DateTime, Text = zoned.ToDateTimeOffset().ToString("O", CultureInfo.InvariantCulture) },
        DateTimeOffset at => new() { Kind = OwnerGraphValue.DateTime, Text = at.ToString("O", CultureInfo.InvariantCulture) },
        IEnumerable<object> items => new() { Kind = OwnerGraphValue.List, Items = [.. items.Select(ToValue)] },
        // A type this format cannot carry is refused, not dropped: an export that silently loses a field is a lossy copy.
        _ => throw new NotSupportedException($"An identity property of type {value?.GetType().Name ?? "null"} cannot be exported."),
    };

    internal static object? FromValue(OwnerGraphValue value) => value.Kind switch
    {
        OwnerGraphValue.String => value.Text,
        OwnerGraphValue.Integer => value.Long,
        OwnerGraphValue.Number => value.Double,
        OwnerGraphValue.Boolean => value.Flag,
        OwnerGraphValue.DateTime => DateTimeOffset.Parse(value.Text!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        OwnerGraphValue.List => (value.Items ?? []).Select(FromValue).ToList(),
        _ => throw new NotSupportedException($"Unknown identity value kind '{value.Kind}'."),
    };

    // Stored records keep their text readable ("Córdoba", not an escape); either reads back the same.
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The rewriting for one import: old owner to new, and the old-to-new ids and observation keys.</summary>
    internal sealed class Remap(string oldOwner, string newOwner, IReadOnlyDictionary<string, string> ids,
        IReadOnlyDictionary<string, string> keys)
    {
        private readonly string _oldScope = "owned:" + oldOwner;
        private readonly string _newScope = "owned:" + newOwner;

        public OwnerGraphValue Value(string property, OwnerGraphValue value) => value.Kind switch
        {
            OwnerGraphValue.String => value with { Text = Text(property, value.Text!) },
            OwnerGraphValue.List => value with { Items = [.. (value.Items ?? []).Select(item => Value(property, item))] },
            _ => value,
        };

        public string Id(string id) => ids.GetValueOrDefault(id, id);

        private string Text(string property, string text)
        {
            if (IsOwner(property) && text == oldOwner) return newOwner;
            var leaf = Leaf(text);
            if (!string.Equals(leaf, text, StringComparison.Ordinal)) return leaf;
            return text.Length > 1 && text[0] is '{' or '[' ? Json(text) : text;
        }

        // A value that IS a reference: an id, an observation key or the owner's scope, compared whole.
        private string Leaf(string text) =>
            ids.TryGetValue(text, out var id) ? id
            : keys.TryGetValue(text, out var key) ? key
            : text == _oldScope ? _newScope
            : text;

        // REV1-L7: JSON is written again only when something in it changed. Unchanged JSON keeps its exact text, so an
        // evidence payload (hashed into its assertion's id) and a quote that happens to be JSON come through as they were.
        private string Json(string text)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(text); }
            catch (JsonException) { return text; }
            if (root is null) return text;
            return Walk(root, null) ? root.ToJsonString(Readable) : text;
        }

        /// <summary>Rewrites references under <paramref name="node"/> in place; true when anything changed.</summary>
        private bool Walk(JsonNode node, string? property)
        {
            var changed = false;
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (name, child) in obj.ToList())
                    {
                        var next = child;
                        if (child is JsonValue value && value.TryGetValue<string>(out var s))
                        {
                            var replaced = Replace(name, s);
                            if (!string.Equals(replaced, s, StringComparison.Ordinal)) { next = JsonValue.Create(replaced); changed = true; }
                        }
                        else if (child is not null) changed |= Walk(child, name);
                        // An id can be a property name too: a contextual confirmation's fingerprints are keyed by candidate (PR260-F7).
                        var key = Leaf(name);
                        if (!string.Equals(key, name, StringComparison.Ordinal)) { obj.Remove(name); obj[key] = next; changed = true; }
                        else if (!ReferenceEquals(next, child)) obj[name] = next;
                    }
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (array[i] is JsonValue value && value.TryGetValue<string>(out var s))
                        {
                            var replaced = Replace(property, s);
                            if (!string.Equals(replaced, s, StringComparison.Ordinal)) { array[i] = JsonValue.Create(replaced); changed = true; }
                        }
                        else if (array[i] is { } child) changed |= Walk(child, property);
                    }
                    break;
            }
            return changed;
        }

        // JSON kept as a string inside JSON (a decision's receipt) is rewritten too (PR260-F7).
        private string Replace(string? property, string text)
        {
            if (property is not null && IsOwner(property) && text == oldOwner) return newOwner;
            var leaf = Leaf(text);
            if (!string.Equals(leaf, text, StringComparison.Ordinal)) return leaf;
            return text.Length > 1 && text[0] is '{' or '[' ? Json(text) : text;
        }

        private static bool IsOwner(string property) =>
            property.Equals("owner_id", StringComparison.OrdinalIgnoreCase) || property.Equals("ownerId", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("owner", StringComparison.OrdinalIgnoreCase);
    }
}
