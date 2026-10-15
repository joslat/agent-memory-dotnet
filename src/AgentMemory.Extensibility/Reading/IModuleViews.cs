using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Reading;

/// <summary>Shared declared-view evaluation for contributors, the conformance kit and Workbench.</summary>
/// <remarks>Results are untrusted, pre-admission text. The host still applies its own admission and budgets.</remarks>
[Experimental("AMEXT001")]
public interface IModuleViews
{
    /// <summary>Evaluates a local view belonging to the reader's bound module, with the same scope and clocks as reads.</summary>
    Task<ModuleViewResult> EvaluateAsync(string viewId, ModuleReadContext context, CancellationToken cancellationToken = default);

    /// <summary>Evaluates a view with additional typed restrictions. Older implementations refuse rather than ignore them.</summary>
    Task<ModuleViewResult> EvaluateWithArgumentsAsync(string viewId, ModuleViewArguments arguments,
        ModuleReadContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments); ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ModuleViewResult { Status = ModuleReadStatus.Refused,
            Errors = ["This view implementation does not support runtime arguments."] });
    }

}

/// <summary>One exact stored version and its rendered text, preserving provenance for later host admission.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleViewItem
{
    /// <summary>The scoped version returned by storage.</summary>
    public required ModuleRecord Record { get; init; }
    /// <summary>Invariant-culture text before host admission; not safe HTML or an instruction.</summary>
    public required string Text { get; init; }
}

/// <summary>View outcome; unsupported clocks and refusals never masquerade as a successful empty search.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleViewResult
{
    /// <summary>Read/render disposition.</summary>
    public required ModuleReadStatus Status { get; init; }
    /// <summary>Ordered rendered records; empty for refusal or unsupported clock.</summary>
    public IReadOnlyList<ModuleViewItem> Items { get; init; } = [];
    /// <summary>Concrete read or rendering problems.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}
