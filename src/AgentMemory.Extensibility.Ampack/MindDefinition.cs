// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>A parsed, verified-member Mind description. Reading it neither activates behaviour nor grants tools.</summary>
[Experimental("AMEXT001")]
public sealed record MindDefinition
{
    /// <summary>The verified composite identity.</summary>
    public required string Id { get; init; }
    /// <summary>The exact composite content.</summary>
    public required string Digest { get; init; }
    /// <summary>The declared versioned job.</summary>
    public required string Profile { get; init; }
    /// <summary>The invariant every-turn core, outside relevance ranking.</summary>
    public required string Core { get; init; }
    /// <summary>The SHA-256 of the invariant UTF-8 core.</summary>
    public required string CoreHash { get; init; }
    /// <summary>Explicit lesser mode: values, character and stance, with no job or capability claims.</summary>
    public required string CharacterCore { get; init; }
    /// <summary>Each signed stage's complete core variant.</summary>
    public IReadOnlyDictionary<string, string> Stages { get; init; } = new Dictionary<string, string>().AsReadOnly();
    /// <summary>The signed closed behaviour contract.</summary>
    public required JsonElement Contract { get; init; }
    /// <summary>Verified members whose policy and lifetime must be rechecked at use.</summary>
    public IReadOnlyList<LoadedPack> Members { get; init; } = [];
    /// <summary>Tools requested by the behaviour; declarations are not registered handlers or grants.</summary>
    public IReadOnlyList<MindToolContract> Tools { get; init; } = [];
    /// <summary>Needed versioned capability sets.</summary>
    public IReadOnlyList<string> NeededCapabilities { get; init; } = [];
    /// <summary>Optional versioned capability sets.</summary>
    public IReadOnlyList<string> OptionalCapabilities { get; init; } = [];
    /// <summary>Machine-readable knowledge requirements; host adapters verify actual content.</summary>
    public IReadOnlyList<JsonElement> KnowledgeNeeds { get; init; } = [];
    /// <summary>Whether a values member was explicitly selected.</summary>
    public bool HasDeclaredValues { get; init; }
    /// <summary>Returns invariant core or one exact signed stage variant; never selects a stage from retrieved text.</summary>
    public string CoreForStage(string? key) => key is null ? Core : Stages.TryGetValue(key, out var value)
        ? value : throw new InvalidDataException("The host selected an unknown signed Mind stage.");
}

/// <summary>A signed request for a host-owned tool with closed-root input and result schemas.</summary>
[Experimental("AMEXT001")]
public sealed record MindToolContract
{
    /// <summary>Stable operation name.</summary>
    public required string Name { get; init; }
    /// <summary>Operation interface version.</summary>
    public required string Version { get; init; }
    /// <summary>The versioned capability set it belongs to.</summary>
    public required string Set { get; init; }
    /// <summary>Publisher's stated purpose.</summary>
    public required string Purpose { get; init; }
    /// <summary>Whether full mode needs this operation.</summary>
    public bool Needed { get; init; }
    /// <summary>The closed input contract; property names are data, not grants.</summary>
    public required JsonElement InputSchema { get; init; }
    /// <summary>The closed result contract.</summary>
    public required JsonElement ResultSchema { get; init; }
}
