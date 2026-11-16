// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>A verified source used to produce a pack, including its declared licence and confidence ceiling.</summary>
[Experimental("AMEXT001")]
public sealed record PackDerivationSource
{
    /// <summary>The source content digest.</summary>
    public required string Digest { get; init; }
    /// <summary>The name referenced by a record's source key.</summary>
    public required string Name { get; init; }
    /// <summary>The publisher's declared licence, if supplied.</summary>
    public string? Licence { get; init; }
    /// <summary>The source confidence ceiling in millionths, if supplied.</summary>
    public int? ConfidenceMicro { get; init; }
}

/// <summary>Read-only provenance projected from the same signed bytes and strict parser used during verification.</summary>
[Experimental("AMEXT001")]
public sealed record PackDerivationInfo
{
    /// <summary>Declared input sources.</summary>
    public IReadOnlyList<PackDerivationSource> Inputs { get; init; } = [];
    /// <summary>The tool that produced the pack.</summary>
    public required string ToolName { get; init; }
    /// <summary>The producing tool's version.</summary>
    public required string ToolVersion { get; init; }
    /// <summary>The optional producing model.</summary>
    public string? ModelId { get; init; }
    /// <summary>The optional model version.</summary>
    public string? ModelVersion { get; init; }
    /// <summary>The optional prompt digest.</summary>
    public string? PromptDigest { get; init; }
    /// <summary>Declared transformation parameters.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>().AsReadOnly();
    /// <summary>The optional parent pack identity; a lineage claim, never inherited trust.</summary>
    public string? ParentId { get; init; }
    /// <summary>The optional parent content digest.</summary>
    public string? ParentDigest { get; init; }
    /// <summary>The declared transformation actions.</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];

    internal static PackDerivationInfo Of(PackDerivation value) => new()
    {
        Inputs = value.Inputs.Select(i => new PackDerivationSource { Digest = i.Digest, Name = i.Name,
            Licence = i.Licence, ConfidenceMicro = i.ConfidenceMicro }).ToList().AsReadOnly(),
        ToolName = value.ToolName, ToolVersion = value.ToolVersion, ModelId = value.ModelId, ModelVersion = value.ModelVersion,
        PromptDigest = value.PromptDigest, Parameters = new Dictionary<string, string>(value.Parameters, StringComparer.Ordinal).AsReadOnly(),
        ParentId = value.ParentId, ParentDigest = value.ParentDigest, Actions = value.Actions.ToList().AsReadOnly(),
    };
}
