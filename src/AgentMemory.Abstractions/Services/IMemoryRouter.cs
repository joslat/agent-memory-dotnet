using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Chooses which memory kinds a recall reads for one question (PLAN 40.56). Deterministic and free: it runs on every turn
/// when routing is enabled (<c>MemoryOptions.Routing</c>), so it must not call a model.
/// </summary>
public interface IMemoryRouter
{
    /// <summary>The route for <paramref name="question"/>: whether to recall, and which kinds.</summary>
    MemoryRoute Route(string question);
}
