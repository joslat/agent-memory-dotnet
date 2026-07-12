namespace AgentMemory.Neo4j.Schema.Parity;

/// <summary>
/// A normalized, comparable view of a memory-graph schema — the set of node labels, relationship-type
/// strings, and property names. Both the live .NET schema (reflected from
/// <see cref="AgentMemory.Abstractions.Schema.SchemaConstants"/>) and a captured upstream snapshot are
/// projected into this shape so they can be compared by <see cref="SchemaParityVerifier"/>.
/// </summary>
internal sealed record SchemaDescriptor(
    string Version,
    IReadOnlySet<string> NodeLabels,
    IReadOnlySet<string> RelationshipTypes,
    IReadOnlySet<string> Properties);
