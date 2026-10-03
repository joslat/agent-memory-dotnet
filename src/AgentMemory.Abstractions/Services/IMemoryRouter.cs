using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Chooses which memory kinds a recall reads for one question (PLAN 40.56). Deterministic and free: it runs on every turn
/// when routing is enabled (<c>MemoryOptions.Routing</c>), so it must not call a model.
/// </summary>
public interface IMemoryRouter
{
    /// <summary>The route for <paramref name="question"/>: whether to recall, and which kinds.</summary>
    MemoryRoute Route(string question);

    /// <summary>
    /// The route for <paramref name="question"/> with <paramref name="additionalRules"/> evaluated beside the configured
    /// ones: how a module's own kinds are chosen (Extensibility 0.10), by the same engine and the same question as core
    /// recall, so the two never disagree about the core kinds. The rules come from outside the host's configuration (a
    /// module's manifest), so they are held to <see cref="Check"/>'s rules.
    /// </summary>
    MemoryRoute Route(string question, IReadOnlyList<MemoryRoutingRule> additionalRules);

    /// <summary>
    /// What is wrong with <paramref name="rules"/> before they are used: an empty kind or name, a pattern that does not
    /// compile, or one the router will not run on untrusted text (backtracking constructs). Empty when nothing is.
    /// </summary>
    IReadOnlyList<string> Check(IReadOnlyList<MemoryRoutingRule> rules);
}
