using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Contributors;

/// <summary>
/// A module's share of traversal context (coding packs CMP-1): the records of this module that the turn's walk reached, best
/// first, each with the path that reached it as its reason. Added for every module whose manifest declares
/// <see cref="ModuleManifest.Traversal"/>; the walk itself runs once per request and is shared by all of them.
/// </summary>
[Experimental("AMEXT001")]
public sealed class TraversalContributor : IContextContributor
{
    /// <summary>The local id of every module's traversal contributor.</summary>
    public const string LocalId = "traversal";

    private readonly ModuleManifest _module;
    private readonly TraversalService _walks;
    private readonly FrozenSet<string> _hubs;

    /// <inheritdoc/>
    public ContextContributorDescriptor Descriptor { get; }

    internal TraversalContributor(ModuleManifest module, TraversalService walks, int priority = 100)
    {
        _module = module; _walks = walks;
        _hubs = (module.Traversal?.Hubs ?? []).ToFrozenSet(StringComparer.Ordinal);
        var id = ModuleIdentity.OwnedId(module.Id, LocalId);
        Descriptor = new(id, new[] { id }.ToFrozenSet(StringComparer.Ordinal), priority);
    }

    /// <inheritdoc/>
    public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(request.AsOf is null && request.SystemAsOf is null && request.Anchors.Count > 0 && _walks.Available);
    }

    /// <inheritdoc/>
    public async Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken)
        => (await ContributeResultAsync(request, cancellationToken).ConfigureAwait(false)).Section;

    /// <inheritdoc/>
    public async Task<ContextContributionResult> ContributeResultAsync(ContextRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        if (request.AsOf is not null || request.SystemAsOf is not null)
            return new() { OmissionReason = ContextOmissionReason.NotApplicable, Detail = "Traversal reads live records only." };
        if (request.Anchors.Count == 0) return new() { OmissionReason = ContextOmissionReason.NotApplicable, Detail = "No anchors to start from." };
        if (!_walks.Available) return new() { OmissionReason = ContextOmissionReason.NotApplicable, Detail = "The module store cannot walk." };
        var walk = await _walks.WalkAsync(request, cancellationToken).ConfigureAwait(false);
        var mine = walk.Items.Where(i => i.Record.ModuleId == _module.Id && !_hubs.Contains(i.Record.Type))
            .Take(_module.Traversal!.MaxItems).ToList();
        if (mine.Count == 0)
            return walk.Problems.Count == 0
                ? new() { OmissionReason = ContextOmissionReason.SearchedAndEmpty }
                : new() { OmissionReason = ContextOmissionReason.Denied, Detail = string.Join("; ", walk.Problems) };
        var types = _module.Ontology!.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        // A walk a budget stopped says so with what it found: the result stands, and the host sees what was spent.
        var detail = walk.Spent is { StoppedBy: { } budget } spent
            ? $"The walk stopped at its {budget} budget: {spent.Reads} reads, {spent.Visited} records, {spent.Elapsed.TotalMilliseconds:0} ms."
            : null;
        return new() { Detail = detail, Section = new(Descriptor.Id, Descriptor.Id, 1, mine.Select(i => new ContextItem(i.Record.Id, Text(types, i))
        {
            Provenance = new()
            {
                RecordModuleId = i.Record.ModuleId, RecordType = i.Record.Type, RecordVersionId = i.Record.Id, RecordKey = i.Record.Key,
                CreatedAtUtc = i.Record.CreatedAtUtc, InvalidatedAtUtc = i.Record.InvalidatedAtUtc, SourceSessionId = i.Record.SourceSessionId,
                SourceMessageIds = Array.AsReadOnly(i.Record.Source.MessageIds.ToArray()), SourcePack = i.Record.Source.Pack, Via = i.Via
            }
        }).ToList().AsReadOnly()) };
    }

    // The record as its type renders it, then why it is here: the gate judges relevance with the reason in view.
    private static string Text(IReadOnlyDictionary<string, ModuleType> types, TraversalItem item)
    {
        var rendered = types.TryGetValue(item.Record.Type, out var type) ? ModuleRenderTemplate.Render(type, item.Record.Properties) : item.Record.Key;
        return rendered + " (reached from the work in hand: " + item.Via + ")";
    }
}
