using System.Text.Json;

namespace AgentMemory.Core.Resolution;

internal enum IdentityContextualVerdict { Same, Different, Uncertain }
internal sealed record IdentityEvidenceQuote(string SourceId, string Quote);
internal sealed record IdentityContextualResult(IdentityContextualVerdict Verdict, string? CandidateId,
    IReadOnlyList<IdentityEvidenceQuote> Evidence);

/// <summary>
/// Checks structure, allowed identifiers and offered quotes. A valid quote is evidence provenance,
/// not a proof that the model's semantic conclusion is correct; type policy still owns authority.
/// </summary>
internal static class IdentityResponseValidation
{
    internal static IdentityContextualResult? Parse(string? json, IReadOnlySet<string> allowedIds,
        IReadOnlyDictionary<string, string> offeredSources)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 131_072) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (!Fields(root, ["decision", "candidateId", "evidence"])) return null;
            if (!root.TryGetProperty("decision", out var decision) || decision.ValueKind != JsonValueKind.String) return null;
            var verdict = decision.GetString() switch
            {
                "same" => IdentityContextualVerdict.Same,
                "different" => IdentityContextualVerdict.Different,
                "uncertain" => IdentityContextualVerdict.Uncertain,
                _ => (IdentityContextualVerdict?)null
            };
            if (verdict is null || !root.TryGetProperty("candidateId", out var candidate) ||
                candidate.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return null;
            var id = candidate.ValueKind == JsonValueKind.String ? candidate.GetString() : null;
            if (id is not null && !allowedIds.Contains(id)) return null;
            if (verdict != IdentityContextualVerdict.Uncertain && string.IsNullOrWhiteSpace(id)) return null;
            if (!root.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() > 32) return null;
            var quotes = new List<IdentityEvidenceQuote>();
            foreach (var item in evidence.EnumerateArray())
            {
                if (!Fields(item, ["sourceId", "quote"]) || !item.TryGetProperty("sourceId", out var source) ||
                    !item.TryGetProperty("quote", out var quote) || source.ValueKind != JsonValueKind.String || quote.ValueKind != JsonValueKind.String) return null;
                var sourceId = source.GetString();
                var text = quote.GetString();
                if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(text) || text.Length > 2_048 ||
                    !offeredSources.TryGetValue(sourceId, out var original) || !original.Contains(text, StringComparison.Ordinal)) return null;
                var supported = new IdentityEvidenceQuote(sourceId, text);
                if (!quotes.Contains(supported)) quotes.Add(supported);
            }
            if (verdict != IdentityContextualVerdict.Uncertain && quotes.Count == 0) return null;
            return new IdentityContextualResult(verdict.Value, id, quotes.ToArray());
        }
        catch (JsonException) { return null; }
    }

    private static bool Fields(JsonElement element, IReadOnlyList<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().All(property => allowed.Contains(property.Name) && seen.Add(property.Name));
    }
}
