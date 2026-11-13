using System.Collections.ObjectModel;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Resolution;

namespace AgentMemory.Core.Extraction;

/// <summary>Freezes application assertions independently of the model's observation and source roots.</summary>
internal static class IdentityHostContext
{
    internal static ExtractedEntity FreezeMention(Entity original) => new()
    {
        Name = original.Name, Type = original.Type, Subtype = original.Subtype,
        Description = original.Description, Confidence = original.Confidence,
        Aliases = Array.AsReadOnly(original.Aliases.ToArray()),
        Attributes = new ReadOnlyDictionary<string, object>(original.Attributes.ToDictionary(pair => pair.Key,
            pair => pair.Value, StringComparer.Ordinal))
    };

    internal static IdentityDecisionWrite Apply(IdentityDecisionWrite initial, IdentityHostEvidence? evidence, string? owner)
    {
        if (evidence is null) return initial;
        if (evidence.Keys is null || evidence.Keys.Count > 32)
            throw new ArgumentException("Host identity keys exceed their bound.");
        foreach (var key in evidence.Keys)
        {
            if (key is null || key.Values is null || key.Values.Count > 32)
                throw new ArgumentException("Host identity key components exceed their bound.");
            Text(key.Namespace); Text(key.Authority); Text(key.SourceId);
            foreach (var component in key.Values) { Text(component.Key); Text(component.Value); }
        }
        if (evidence.Kind is { } kind)
        {
            if (kind.Kind is not (IdentitySubjectKind.Reference or IdentitySubjectKind.CalendarDate or IdentitySubjectKind.CalendarInterval))
                throw new ArgumentException("A host semantic-kind assertion needs a concrete supported kind.");
            Text(kind.Authority); Text(kind.SourceId);
        }
        if (evidence.NameNamespace is { } ns) { Text(ns.Namespace); Text(ns.Authority); Text(ns.SourceId); }
        var context = initial.Decision.Context with
        {
            VerifiedKeys = evidence.Keys, VerifiedKind = evidence.Kind,
            NameNamespace = evidence.NameNamespace?.Namespace, NameNamespaceEvidence = evidence.NameNamespace
        };
        // The bounded serializer deep-copies mutable returned collections before any candidate await.
        return IdentityDecisionPayload.Capture(initial with { Decision = initial.Decision with { Context = context } }, owner);
    }

    private static void Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
            throw new ArgumentException("Host identity assertions need bounded nonempty values, authority and source.");
    }
}
