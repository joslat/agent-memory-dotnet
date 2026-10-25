using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Modules;

/// <summary>The host features a module can require, in one place for the reader, validator, resolvers and CLI.</summary>
[Experimental("AMEXT001")]
public static class ModuleHostFeatures
{
    /// <summary>Retained versions of core memories (<c>IMemoryHistoryService</c>).</summary>
    public const string History = "History";

    /// <summary>Prospective firing of reminders on current recall.</summary>
    public const string Reminders = "Reminders";

    /// <summary>A chat model the host grants to modules; model access is composition, never ambient discovery.</summary>
    public const string Inference = "Inference";

    /// <summary>Module-owned schema in the bound store; derived from a manifest's ontology or views, never declared.</summary>
    public const string ModuleSchema = "ModuleSchema";

    /// <summary>The features a manifest may declare in <c>requires.host</c>.</summary>
    public static IReadOnlySet<string> Declarable { get; } = new[] { History, Reminders, Inference }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Every feature a host can offer, the derived one included.</summary>
    public static IReadOnlySet<string> Known { get; } = new[] { History, Reminders, Inference, ModuleSchema }.ToFrozenSet(StringComparer.Ordinal);
}
