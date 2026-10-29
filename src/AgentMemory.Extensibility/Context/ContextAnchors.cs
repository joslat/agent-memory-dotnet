using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Context;

/// <summary>
/// The anchors of the work in hand, for hosts that do not build <see cref="ContextRequest"/> themselves (coding packs CMP-1): an
/// IDE extension or an agent host opens a scope with the files and symbols of the turn, and the extensible provider passes them
/// to traversal context. Flows with the async context, like the owner scope.
/// </summary>
/// <example><code>
/// using (ContextAnchors.Begin([ContextAnchor.Path("src/Store/ModuleStore.cs", "editor")]))
///     await agent.RunAsync(question, session);
/// </code></example>
[Experimental("AMEXT001")]
public static class ContextAnchors
{
    private static readonly AsyncLocal<IReadOnlyList<ContextAnchor>?> Ambient = new();

    /// <summary>The anchors of the innermost open scope; empty outside any.</summary>
    public static IReadOnlyList<ContextAnchor> Current => Ambient.Value ?? [];

    /// <summary>Opens a scope with these anchors until the returned handle is disposed; the outer scope comes back after.</summary>
    public static IDisposable Begin(IEnumerable<ContextAnchor> anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        var previous = Ambient.Value;
        // The first MaximumPerRequest distinct anchors, as the walk takes them (review 2: duplicates used up places here first).
        Ambient.Value = Traversal.TraversalWalk.Anchors(anchors).AsReadOnly();
        return new Restore(previous);
    }

    private sealed class Restore(IReadOnlyList<ContextAnchor>? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }
}
