using AgentMemory.Neo4j.Schema.Parity;

namespace AgentMemory.Neo4j.Schema.Extensions;

/// <summary>
/// The schema as it actually is once active extensions are counted: base ∪ active declarations.
/// </summary>
/// <remarks>
/// <para>
/// A pure function computed at the edge. Base <c>SchemaConstants</c> is never edited by an extension —
/// that is R1 restated as an implementation rule, and it is what makes the byte-identical-when-off
/// guarantee checkable rather than asserted: with no active extensions this returns a descriptor
/// set-for-set equal to <see cref="DotNetSchema.Describe"/>.
/// </para>
/// <para>
/// <see cref="SchemaParityVerifier.Verify"/> is unchanged and unaware of extensions. It already takes
/// both the descriptor and the policy as parameters, so composing them here means the comparison
/// algorithm never had to learn a new concept.
/// </para>
/// </remarks>
internal static class EffectiveSchema
{
    /// <summary>Base ∪ the declarations of <paramref name="active"/>.</summary>
    public static SchemaDescriptor Describe(IReadOnlyList<ISchemaExtension> active) =>
        Describe(DotNetSchema.Describe(), active);

    /// <summary>The testable overload: any base descriptor ∪ the declarations of <paramref name="active"/>.</summary>
    public static SchemaDescriptor Describe(SchemaDescriptor baseSchema, IReadOnlyList<ISchemaExtension> active)
    {
        ArgumentNullException.ThrowIfNull(baseSchema);
        ArgumentNullException.ThrowIfNull(active);

        // The identity case is returned by reference, not rebuilt. Rebuilding would produce an equal
        // descriptor today and would quietly stop doing so the first time a union step gained a
        // normalisation the base path does not have.
        if (active.Count == 0) return baseSchema;

        var labels = new HashSet<string>(baseSchema.NodeLabels, StringComparer.Ordinal);
        var relationshipTypes = new HashSet<string>(baseSchema.RelationshipTypes, StringComparer.Ordinal);
        var properties = new HashSet<string>(baseSchema.Properties, StringComparer.Ordinal);

        foreach (var extension in active)
        {
            labels.UnionWith(extension.DeclaredLabels);
            relationshipTypes.UnionWith(extension.DeclaredRelationshipTypes);
            foreach (var declared in extension.DeclaredProperties.Values)
                properties.UnionWith(declared);
        }

        return baseSchema with
        {
            NodeLabels = labels,
            RelationshipTypes = relationshipTypes,
            Properties = properties,
        };
    }
}
