namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Controls how <see cref="AgentMemory.Abstractions.Services.IMemoryIsolationPolicy"/> resolves a tenant
/// operation that has no explicit owner scope. See <c>docs/getting-started.md</c> "Owner isolation" for
/// the production guidance on choosing a mode.
/// </summary>
public enum MemoryIsolationMode
{
    /// <summary>
    /// The default, backward-compatible behavior: an unscoped tenant read returns global memory, and a
    /// write without an owner creates shared memory. Explicit owner scopes still isolate normally.
    /// </summary>
    SingleTenant,

    /// <summary>
    /// Same resolution as <see cref="SingleTenant"/>, but logs a structured warning (operation name, mode,
    /// read/write, global-or-shared — never memory content) whenever a tenant operation resolves to
    /// unscoped/global. Use this to discover unscoped call sites before switching to
    /// <see cref="StrictMultiTenant"/>.
    /// </summary>
    WarnOnUnscoped,

    /// <summary>
    /// Fails closed: a tenant operation that has no owner throws
    /// <see cref="AgentMemory.Abstractions.Exceptions.MemoryOwnerScopeRequiredException"/> before any
    /// repository access. Administrative operations are unaffected — they always have an explicit,
    /// separate access path.
    /// </summary>
    StrictMultiTenant
}
