using System.Buffers;
using System.Text;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Resolution;

/// <summary>Bounded untrusted model data; configured attributes never execute host getters.</summary>
internal static class IdentityContextPayload
{
    public static bool TryEncode(ExtractedEntity mention, IReadOnlyList<IdentityCandidate> candidates,
        EntityIdentityResolutionContext context, IReadOnlyDictionary<string, string> offeredSources,
        EntityIdentityPolicy policy, int maximumBytes, out string json, out string diagnostic)
    {
        json = ""; diagnostic = "contextual_context_budget";
        if (maximumBytes <= 0 || context.Sources.Count > 256 || candidates.Count > 64 ||
            context.Sources.Any(source => source.OriginIds.Count > 256) ||
            candidates.Any(candidate => candidate.Entity.SourceMessageIds.Count > 256)) return false;

        var own = new Dictionary<string, object?>
        { ["Name"] = mention.Name, ["Type"] = mention.Type, ["Description"] = mention.Description };
        if (!Project(own, mention.Attributes, policy.ContextProperties))
        { diagnostic = "contextual_invalid_property"; return false; }
        var offered = new List<Dictionary<string, object?>>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var entity = candidate.Entity;
            var data = new Dictionary<string, object?>
            {
                ["id"] = entity.EntityId, ["Name"] = entity.Name, ["Type"] = entity.Type,
                ["Description"] = entity.Description,
                ["sourceIds"] = entity.SourceMessageIds.Where(offeredSources.ContainsKey).ToArray()
            };
            if (!Project(data, entity.Attributes, policy.ContextProperties))
            { diagnostic = "contextual_invalid_property"; return false; }
            offered.Add(data);
        }
        var payload = new
        {
            mention = own, candidates = offered,
            sources = context.Sources.Select(source => new { source.SourceId, source.Text,
                origins = source.OriginIds.Distinct(StringComparer.Ordinal).ToArray() }).ToArray()
        };
        try
        {
            var buffer = new BudgetBuffer(maximumBytes);
            using (var writer = new Utf8JsonWriter(buffer, new() { MaxDepth = 16 }))
            { JsonSerializer.Serialize(writer, payload); writer.Flush(); }
            json = Encoding.UTF8.GetString(buffer.Written);
            return true;
        }
        catch (BudgetExceededException) { return false; }
    }

    private static bool Project(Dictionary<string, object?> target, IReadOnlyDictionary<string, object> attributes,
        IReadOnlyList<string> properties)
    {
        if (properties.Count == 0) return true;
        var selected = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in properties)
        {
            if (!attributes.TryGetValue(name, out var value)) continue;
            if (!Scalar(value)) return false;
            selected.Add(name, value);
        }
        target.Add("contextProperties", selected);
        return true;
    }

    private static bool Scalar(object? value) => value switch
    {
        null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal => true,
        float number => float.IsFinite(number),
        double number => double.IsFinite(number),
        JsonElement element => element.ValueKind is JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number
            or JsonValueKind.True or JsonValueKind.False,
        _ => false
    };

    private sealed class BudgetExceededException : Exception { }

    private sealed class BudgetBuffer(int maximumBytes) : IBufferWriter<byte>
    {
        private readonly ArrayBufferWriter<byte> _buffer = new(4096);
        // Utf8JsonWriter reserves worst-case escaping space before committing actual output.
        // Allow that bounded reservation while enforcing the exact emitted-byte limit in Advance.
        private readonly int _reservationLimit = checked(maximumBytes * 6 + 4096);
        public ReadOnlySpan<byte> Written => _buffer.WrittenSpan;
        public void Advance(int count)
        {
            if (count < 0 || count > maximumBytes - _buffer.WrittenCount) throw new BudgetExceededException();
            _buffer.Advance(count);
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var remaining = _reservationLimit - _buffer.WrittenCount;
            if (sizeHint < 0 || Math.Max(1, sizeHint) > remaining) throw new BudgetExceededException();
            var memory = _buffer.GetMemory(sizeHint);
            return memory[..Math.Min(memory.Length, remaining)];
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
