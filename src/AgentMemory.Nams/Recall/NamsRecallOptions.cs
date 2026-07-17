namespace AgentMemory.Nams.Recall;

/// <summary>
/// Static recall behavior knobs. Per-turn specifics (the current query text) are passed directly to
/// <see cref="INamsRecallService.RecallAsync"/> instead of living here. This is a package-local replacement
/// for reusing the direct backend's <c>IAutomaticRecallPolicy</c>, which lives in
/// <c>AgentMemory.AgentFramework</c> and is therefore unreachable from <c>AgentMemory.Nams</c> (B9) --
/// Phase 6 is where the real policy's decisions get translated into calls against this simpler surface.
/// </summary>
public sealed class NamsRecallOptions
{
    public bool IncludeEntitySearch { get; set; } = true;

    /// <summary>Default of 5 keeps a typical recall payload small; not tied to any NAMS-side limit.</summary>
    public int EntitySearchLimit { get; set; } = 5;

    /// <summary>A local safety-net character budget applied by <see cref="NamsRecallService"/> -- not the
    /// real, shared token-based budget (<c>ContextFormatOptions</c>), which Phase 6 applies on top when
    /// composing the final prompt. Default of 8000 is a conservative pre-token-budget ceiling, not a tuned
    /// value against any specific model's context window.</summary>
    public int MaxTotalCharacters { get; set; } = 8000;
}
