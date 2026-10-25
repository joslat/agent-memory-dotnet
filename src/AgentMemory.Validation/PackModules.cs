using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Reading;

namespace AgentMemory.Validation;

/// <summary>
/// A pack's module part (40.26, the conformance kit): the modules it runs against, the proposals each teach step submits
/// through the real ingestion pipeline, the module records the store must then hold, and the views that must return
/// them. Deterministic: the pack plays the model, as it does for core extraction; the host composes the modules.
/// </summary>
[Experimental("AMEXT001")]
public sealed record PackModules
{
    /// <summary>The modules the pack needs, each with a version range; the run fails when the host did not compose them.</summary>
    public IReadOnlyList<PackModuleRequirement> Require { get; init; } = [];

    /// <summary>The teach steps, run in order after the core sessions are in.</summary>
    public IReadOnlyList<PackTeach> Teach { get; init; } = [];

    /// <summary>What the module store must hold once every step has run.</summary>
    public IReadOnlyList<PackModuleRecord> Records { get; init; } = [];

    /// <summary>The views to evaluate, each in a fresh session of its own.</summary>
    public IReadOnlyList<PackModuleView> Views { get; init; } = [];
}

/// <summary>A module a pack needs.</summary>
[Experimental("AMEXT001")]
public sealed record PackModuleRequirement
{
    /// <summary>The module id.</summary>
    public required string Id { get; init; }

    /// <summary>The version range it must be in, such as <c>[1.0.0,2.0.0)</c>.</summary>
    public required string Version { get; init; }
}

/// <summary>
/// One teach step: recorded proposals a provider submits for one of the pack's sessions. In a proposal's source message
/// ids, <c>#n</c> names the session's nth message (from 0); the runner gives the stored id.
/// </summary>
[Experimental("AMEXT001")]
public sealed record PackTeach
{
    /// <summary>A stable id, unique in the pack.</summary>
    public required string Id { get; init; }

    /// <summary>The pack session whose messages are the sources and whose owner writes.</summary>
    public required string Session { get; init; }

    /// <summary>The provider's owned identity, <c>moduleId:local</c>.</summary>
    public required string Provider { get; init; }

    /// <summary><c>EveryTurn</c> (default) or <c>OnRequest</c>.</summary>
    public string Trigger { get; init; } = "EveryTurn";

    /// <summary>The write clock; by default the session's last message time.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>A view of the provider's module whose records the provider is shown before it writes; replacements can name
    /// only those. Null: none.</summary>
    public string? ExistingView { get; init; }

    /// <summary>The recorded proposals, in order.</summary>
    public IReadOnlyList<ModuleProposal> Proposals { get; init; } = [];

    /// <summary>The closing approver's recorded answer for each replacement a proposal names (the pack plays the judge).</summary>
    public IReadOnlyList<PackClosing> Closings { get; init; } = [];

    /// <summary>The outcome each proposal must have.</summary>
    public IReadOnlyList<PackTeachExpect> Expect { get; init; } = [];
}

/// <summary>A recorded judge answer for one replacement.</summary>
[Experimental("AMEXT001")]
public sealed record PackClosing
{
    /// <summary>The replacing proposal's position in the step.</summary>
    public required int Proposal { get; init; }

    /// <summary>The replaced record's key.</summary>
    public required string Key { get; init; }

    /// <summary>The probability the judge gives; the core writer's threshold decides.</summary>
    public required double Probability { get; init; }
}

/// <summary>The outcome one proposal must have.</summary>
[Experimental("AMEXT001")]
public sealed record PackTeachExpect
{
    /// <summary>The proposal's position in the step.</summary>
    public required int Proposal { get; init; }

    /// <summary><c>Stored</c>, <c>NoOp</c>, <c>Invalidated</c> or <c>Refused</c>.</summary>
    public required string Status { get; init; }

    /// <summary>The keys its approved replacements must close, when it names any.</summary>
    public IReadOnlyList<string> Closed { get; init; } = [];
}

/// <summary>A module record the store must hold, or must not.</summary>
[Experimental("AMEXT001")]
public sealed record PackModuleRecord
{
    /// <summary>A stable id, unique in the pack.</summary>
    public required string Id { get; init; }

    /// <summary>The owner whose record it is.</summary>
    public required string Owner { get; init; }

    /// <summary>The module id.</summary>
    public required string Module { get; init; }

    /// <summary>The module's type.</summary>
    public required string Type { get; init; }

    /// <summary>The logical key.</summary>
    public required string Key { get; init; }

    /// <summary><c>live</c>, <c>closed</c> (every version invalidated) or <c>absent</c>.</summary>
    public required string State { get; init; }

    /// <summary>Values the live version must carry.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>How many versions the key must have, when given.</summary>
    public int? Versions { get; init; }
}

/// <summary>A view evaluation and what it must return.</summary>
[Experimental("AMEXT001")]
public sealed record PackModuleView
{
    /// <summary>A stable id, unique in the pack.</summary>
    public required string Id { get; init; }

    /// <summary>The owner who reads.</summary>
    public required string Owner { get; init; }

    /// <summary>The module id.</summary>
    public required string Module { get; init; }

    /// <summary>The module's declared view.</summary>
    public required string View { get; init; }

    /// <summary>Runtime arguments that narrow the view.</summary>
    public ModuleViewArguments? Arguments { get; init; }

    /// <summary>A transaction-time pin: the view as the store held it then.</summary>
    public DateTimeOffset? SystemAsOf { get; init; }

    /// <summary>The keys it must return, in any order, and no others (each once: a history view returns a key's versions).</summary>
    public IReadOnlyList<string> ExpectKeys { get; init; } = [];

    /// <summary>Text the rendered items must contain.</summary>
    public IReadOnlyList<string> RequiredText { get; init; } = [];

    /// <summary>Text no rendered item may contain.</summary>
    public IReadOnlyList<string> ExcludedText { get; init; } = [];
}
