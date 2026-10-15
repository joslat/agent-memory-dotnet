using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentMemory.Extensibility.Context;

/// <summary>One bounded, keyed module item before host admission, deduplication or optional judging.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleContextCandidate
{
    /// <summary>Stable hash of metadata binding and item/version identity. Not an authorization token.</summary>
    public required string Key { get; init; }
    /// <summary>Host-stamped module, contributor and section metadata.</summary>
    public required ContextModuleBinding Binding { get; init; }
    /// <summary>Contributor-supplied stable id, or null for an ordinal-only item.</summary>
    public string? ItemId { get; init; }
    /// <summary>Original position within the section; not a persistent identity.</summary>
    public required int ItemIndex { get; init; }
    /// <summary>Exact untrusted text; admission is still required before any judge or model.</summary>
    public required string Text { get; init; }
    /// <summary>Optional copied record-version/source claims; not a scope or trust grant.</summary>
    public ContextItemProvenance? Provenance { get; init; }
}

/// <summary>Projection counts are collection counts, not admission or relevance decisions.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleCandidateProjection
{
    /// <summary>Original number of section items.</summary>
    public required int OfferedItems { get; init; }
    /// <summary>Whole items not projected because of the supplied item cap.</summary>
    public required int ItemLimitOmitted { get; init; }
    /// <summary>Bounded immutable projection in the contributor's original order.</summary>
    public IReadOnlyList<ModuleContextCandidate> Candidates { get; init; } = [];
}

/// <summary>Explicit, bounded projection for a host preparing module selection. No services are called.</summary>
/// <remarks>Not invoked by compilation or rendering. Equal identity keys intentionally identify duplicate
/// candidates; callers must resolve duplicates before forming a judge batch. Text equality is not identity.
/// Ordinal-only keys are stable only for the same ordered section. Metadata hashes are not runtime trust.</remarks>
[Experimental("AMEXT001")]
public static class ModuleContextCandidates
{
    /// <summary>Copies at most maxItems from a bound section, preserving exact text and typed source references.</summary>
    /// <remarks>The cap bounds item projection, not arbitrary contributor code or source-list size.
    /// Unbound/relabeled sections and inconsistent record ids are refused, never assigned guessed ownership.</remarks>
    public static ModuleCandidateProjection Project(ContextSection section, int maxItems)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentOutOfRangeException.ThrowIfNegative(maxItems);
        var binding = section.Binding ?? throw new ArgumentException("Candidate projection requires a host-bound section.", nameof(section));
        if (!string.Equals(binding.ContributorId, section.ContributorId, StringComparison.Ordinal)
            || !string.Equals(binding.SectionType, section.TypeId, StringComparison.Ordinal))
            throw new ArgumentException("The section no longer matches its host binding.", nameof(section));
        var count = section.Items.Count;
        var take = Math.Min(count, maxItems);
        var candidates = new List<ModuleContextCandidate>(take);
        for (var index = 0; index < take; index++)
        {
            var item = section.Items[index];
            if (item is null || item.Text is null)
                throw new ArgumentException("Candidate items and text must be non-null.", nameof(section));
            var provenance = item.Provenance;
            if (provenance is not null)
            {
                if (string.IsNullOrWhiteSpace(provenance.RecordModuleId) || string.IsNullOrWhiteSpace(provenance.RecordType)
                    || string.IsNullOrWhiteSpace(provenance.RecordVersionId)
                    || !string.Equals(provenance.RecordVersionId, item.Id, StringComparison.Ordinal))
                    throw new ArgumentException("Record provenance must identify this exact item version.", nameof(section));
                provenance = provenance with { SourceMessageIds = Array.AsReadOnly(provenance.SourceMessageIds.ToArray()) };
            }
            // Serialize distinct components: separator characters in opaque ids must not alias another tuple.
            var identity = JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                binding.MetadataFingerprint, binding.ModuleId, binding.ModuleVersion, binding.ContributorId, binding.SectionType,
                provenance?.RecordModuleId, provenance?.RecordType,
                item.Id is null ? "ordinal" : "id", item.Id ?? index.ToString(CultureInfo.InvariantCulture)
            });
            candidates.Add(new()
            {
                Key = "module:" + Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant(),
                Binding = binding, ItemId = item.Id, ItemIndex = index, Text = item.Text, Provenance = provenance
            });
        }
        return new() { OfferedItems = count, ItemLimitOmitted = count - take, Candidates = candidates.AsReadOnly() };
    }
}
