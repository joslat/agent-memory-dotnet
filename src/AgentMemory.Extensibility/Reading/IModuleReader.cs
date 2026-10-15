using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;

namespace AgentMemory.Extensibility.Reading;

/// <summary>Read capability bound by the host to a single calling module, separately from write authority.</summary>
[Experimental("AMEXT001")]
public interface IModuleReader
{
    /// <summary>Immutable calling module identity used for own/dependency access checks.</summary>
    string CallerModuleId { get; }
    /// <summary>Reads scoped versions with valid required anchors. Never executes module code or applies DDL.</summary>
    Task<ModuleReadResult> FindAsync(ModuleQuery query, ModuleReadContext context, CancellationToken cancellationToken = default);
}

/// <summary>One stored module version with copied provenance and resolved declared links.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleRecord
{
    /// <summary>Owning module identity.</summary>
    public required string ModuleId { get; init; }
    /// <summary>Owned type name.</summary>
    public required string Type { get; init; }
    /// <summary>Stable version identity.</summary>
    public required string Id { get; init; }
    /// <summary>Logical key within the module/type/owner bucket.</summary>
    public required string Key { get; init; }
    /// <summary>Owner stamp; null denotes a shared record.</summary>
    public string? OwnerId { get; init; }
    /// <summary>Creation time of this version.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
    /// <summary>Exclusive end of this version's transaction-time interval.</summary>
    public DateTimeOffset? InvalidatedAtUtc { get; init; }
    /// <summary>Declared scalar values as independent JSON values.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = new Dictionary<string, JsonElement>().AsReadOnly();
    /// <summary>Declared links resolved within the permitted scope.</summary>
    public IReadOnlyList<ModuleRecordLink> Links { get; init; } = [];
    /// <summary>Original source session, retained even after source messages are cleared.</summary>
    public required string SourceSessionId { get; init; }
    /// <summary>Copied source identities and bounded excerpt.</summary>
    public required ProposalSource Source { get; init; }
}

/// <summary>A resolved outgoing link; target ownership is enforced by the reader.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleRecordLink
{
    /// <summary>Declared relationship name.</summary>
    public required string Relation { get; init; }
    /// <summary>Resolved target version/entity identity.</summary>
    public required string TargetId { get; init; }
    /// <summary>Declared target type.</summary>
    public required string TargetType { get; init; }
}

/// <summary>Read outcome. Refusal and unsupported-clock results contain no records.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleReadResult
{
    /// <summary>Read disposition.</summary>
    public required ModuleReadStatus Status { get; init; }
    /// <summary>Ordered matching versions.</summary>
    public IReadOnlyList<ModuleRecord> Records { get; init; } = [];
    /// <summary>Concrete refusal or applicability details.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}
