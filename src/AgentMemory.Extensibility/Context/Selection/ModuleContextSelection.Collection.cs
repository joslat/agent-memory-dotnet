using System.Text.Json;
using AgentMemory.Extensibility.Contributors;

namespace AgentMemory.Extensibility.Context;

public sealed partial class ModuleContextSelection
{
    private (List<Entry> Entries, List<ModuleContributorSelection> Contributors) Collect(ContextEnvelope envelope, CancellationToken cancellationToken)
    {
        var entries = new List<Entry>(); var contributors = new List<ModuleContributorSelection>();
        var identities = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var remaining = _options.MaxCandidates;
        var sections = envelope.Sections.Where(s => s.TypeId != CoreMemoryContextContributor.SectionType).ToArray();
        var mixedMetadata = sections.Where(s => s.Binding is not null).Select(s => s.Binding!.MetadataFingerprint)
            .Distinct(StringComparer.Ordinal).Skip(1).Any();
        foreach (var section in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int? offered = null;
            try
            {
                offered = section.Items.Count;
                if (mixedMetadata)
                {
                    contributors.Add(Failed(section, offered, "MixedMetadata"));
                    continue;
                }
                var limit = _options.ContributorItemLimits.GetValueOrDefault(section.ContributorId, _options.MaxItemsPerContributor);
                var projection = ModuleContextCandidates.Project(section, Math.Min(limit, remaining));
                remaining -= projection.Candidates.Count;
                contributors.Add(new() { ContributorId = section.ContributorId, OfferedItems = projection.OfferedItems,
                    ProjectedItems = projection.Candidates.Count, ItemLimitOmitted = projection.ItemLimitOmitted });
                foreach (var candidate in projection.Candidates)
                {
                    var source = candidate.Provenance;
                    var identity = source is null ? candidate.Key : JsonSerializer.Serialize(new[]
                    { candidate.Binding.ModuleId, source.RecordModuleId, source.RecordType, source.RecordVersionId });
                    if (!identities.TryGetValue(identity, out var existing))
                    {
                        var entry = new Entry(candidate); identities.Add(identity, entry); entries.Add(entry);
                    }
                    else
                    {
                        existing.Origins.Add(candidate);
                        if (!string.Equals(existing.Candidate.Text, candidate.Text, StringComparison.Ordinal)
                            || !string.Equals(existing.Candidate.Provenance?.RecordKey, source?.RecordKey, StringComparison.Ordinal))
                            existing.Disposition = ModuleCandidateDisposition.DuplicateConflict;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { contributors.Add(Failed(section, offered, "InvalidCandidateProjection")); }
        }
        return (entries, contributors);
    }

    private static ModuleContributorSelection Failed(ContextSection section, int? offered, string detail) => new()
    {
        ContributorId = section.ContributorId, OfferedItems = offered, ProjectedItems = 0, ItemLimitOmitted = 0,
        Failure = ContextOmissionReason.Unavailable, FailureDetail = detail
    };
}
