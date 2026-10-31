using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Reading;

/// <summary>
/// When a record is true in the world, and when a host stops using it (feature 07, R-CORE-128): instants in UTC, each optional.
/// The one definition of a record's own liveness; a carrier pack's validity is the other half (LIVE-PER-RECORD).
/// </summary>
[Experimental("AMEXT001")]
public sealed record ModuleRecordLife
{
    /// <summary>When it became true in the world; null: always was.</summary>
    public DateTimeOffset? ValidFromUtc { get; init; }
    /// <summary>When it stopped being true, exclusive: by its own <c>validTo</c>, its type's <c>ttlDays</c>, or a record superseding it.</summary>
    public DateTimeOffset? ValidToUtc { get; init; }
    /// <summary>When a host stops using it, true or not, exclusive.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    /// <summary>The date by which a person should check it; carried and shown, it changes no read.</summary>
    public DateTimeOffset? ReviewByUtc { get; init; }

    /// <summary>Whether the record is live at <paramref name="at"/>: valid then, and not expired.</summary>
    public bool IsLiveAt(DateTimeOffset at) =>
        (ValidFromUtc is not { } from || from <= at) && (ValidToUtc is not { } to || at < to) && (ExpiresAtUtc is not { } expires || at < expires);

    /// <summary>Whether a record with this life (none: always) is live at <paramref name="at"/>.</summary>
    public static bool IsLive(ModuleRecordLife? life, DateTimeOffset at) => life is null || life.IsLiveAt(at);
}
