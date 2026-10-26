#pragma warning disable AMEXT001 // CMP-1 anchors come from the Extensibility preview.
namespace AgentMemory.McpHost.Coding.Client;

/// <summary>
/// The anchors a prompt hook sends with recall (coding packs CMP-1): code names and paths the prompt mentions, then the files
/// changed in the working tree. Best effort: a Git failure or a value the content policy refuses is skipped, never fatal.
/// </summary>
internal static class CodingHookAnchors
{
    public static async Task<IReadOnlyList<object>> FindAsync(string prompt, string cwd, Func<string, CancellationToken, Task<string>>? status,
        CodingContentPolicy policy, CancellationToken cancellationToken)
    {
        var anchors = CodingAnchors.FromPrompt(prompt).ToList();
        if (status is not null)
        {
            try { anchors.AddRange(CodingAnchors.FromStatus(await status(cwd, cancellationToken).ConfigureAwait(false))); }
            catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception
                && !cancellationToken.IsCancellationRequested) { }
        }
        var result = new List<object>();
        foreach (var anchor in anchors.DistinctBy(a => (a.Kind, a.Value)).Take(CodingAnchors.Maximum))
        {
            try { policy.CheckText(anchor.Value); }
            catch (ArgumentException) { continue; }
            result.Add(new { kind = anchor.Kind == AgentMemory.Extensibility.Context.ContextAnchorKind.Path ? "path" : "symbol", value = anchor.Value });
        }
        return result;
    }
}
