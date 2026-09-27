using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Services;

/// <summary>
/// 36.3. Which recalled items are the person's own and which are shared knowledge, decided once for every
/// renderer (the MAF mapper and the Core formatter), so the two surfaces cannot label the same item
/// differently.
/// </summary>
/// <remarks>
/// Shared means owner-less, and only when the recall separated the two
/// (<see cref="MemoryContext.SeparatesSharedKnowledge"/>): in a single-tenant store every row is
/// owner-less and all of it is the person's. Without the flag every item stays where it always rendered.
/// </remarks>
internal static class SharedKnowledge
{
    /// <summary>What a shared section says about itself, in both renderers.</summary>
    internal const string Label = "shared knowledge, not about the user";

    /// <summary>Splits <paramref name="items"/> into the person's own and the shared ones, order kept.</summary>
    internal static (IReadOnlyList<T> Own, IReadOnlyList<T> Shared) Split<T>(
        MemoryContext context, IReadOnlyList<T> items, Func<T, string?> ownerOf)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(ownerOf);
        if (!context.SeparatesSharedKnowledge) return (items, Array.Empty<T>());

        var own = new List<T>(items.Count);
        var shared = new List<T>();
        foreach (var item in items)
            (ownerOf(item) is null ? shared : own).Add(item);
        return (own, shared);
    }
}
