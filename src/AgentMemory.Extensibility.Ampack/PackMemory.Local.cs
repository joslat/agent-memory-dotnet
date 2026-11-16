// SPDX-License-Identifier: Apache-2.0
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;

namespace AgentMemory.Extensibility.Ampack;

public sealed partial class PackMemory
{
    /// <summary>
    /// Projects a host-authorized local working copy before filters, search or traversal. The verified pack is never mutated
    /// and local values cannot claim publisher provenance. The host must resolve scope and enforce write protection in storage.
    /// </summary>
    public PackMemory WithLocalChanges(IReadOnlyDictionary<string, IReadOnlyList<ModuleProposal>> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Keys.Any(id => _knowledge.All(p => p.Id != id))) throw new KnowledgePackException(["Local changes name an unmounted pack."]);
        var copies = new List<KnowledgePack>();
        foreach (var pack in _knowledge)
        {
            if (!changes.TryGetValue(pack.Id, out var local) || local.Count == 0) { copies.Add(pack); continue; }
            if (local.Count > KnowledgePack.MaximumRecords) throw new KnowledgePackException(["Too many local changes."]);
            var records = pack.Proposals.ToDictionary(p => (p.Type, p.Key));
            foreach (var proposal in local)
            {
                if (_erasures.Any(e => e.PackId == pack.Id && e.Erases(proposal.Type, proposal.Key))) continue;
                var shape = pack.ValidateLocalAssertion(proposal);
                if (shape.Count > 0) throw new KnowledgePackException(shape);
                if (records.TryGetValue((proposal.Type, proposal.Key), out var original))
                    records[(proposal.Type, proposal.Key)] = proposal with { Life = original.Life };
                else records[(proposal.Type, proposal.Key)] = proposal;
            }
            if (records.Count > KnowledgePack.MaximumRecords) throw new KnowledgePackException(["The working copy exceeds the pack record bound."]);
            foreach (var record in records.Values)
                foreach (var link in record.Links)
                {
                    var relation = pack.Manifest.Ontology!.Relations.Single(r => r.Name == link.Relation && r.From == record.Type);
                    if (relation.To.Contains(':', StringComparison.Ordinal) || !records.ContainsKey((relation.To, link.Target)))
                        throw new KnowledgePackException([$"The local link {link.Relation} names no record in this working copy."]);
                }
            copies.Add(pack.WithProposals(records.Values.ToList().AsReadOnly()));
        }
        return From(copies, erasures: _erasures);
    }
}
