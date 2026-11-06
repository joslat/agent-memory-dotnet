// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Packs;

/// <summary>One record an erasure names: its type and its key, or the salted hash of its key.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleErasedRecord(string Type, string? Key, string? KeyHash);

/// <summary>
/// A verified erasure (feature 08, R-CORE-133): records of a pack, by key or salted key hash, withdrawn from every version and
/// copy. The format verifier reads and verifies the signed statement; a store applies this form of it, erasing the records and
/// keeping a tombstone of each (the pack, the type, the salt and the key's hash, never the key) that no import passes.
/// </summary>
[Experimental("AMEXT001")]
public sealed record ModuleErasure
{
    /// <summary>The pack whose records it erases, in every version.</summary>
    public required string PackId { get; init; }
    /// <summary>The statement's identity: the SHA-256 of what its signer signed.</summary>
    public required string Statement { get; init; }
    /// <summary>When its signer issued it.</summary>
    public required DateTimeOffset IssuedAt { get; init; }
    /// <summary>Why: withdrawn, incorrect, harmful or legal.</summary>
    public required string Reason { get; init; }
    /// <summary>The salt its key hashes use.</summary>
    public required string Salt { get; init; }
    /// <summary>The records it erases.</summary>
    public required IReadOnlyList<ModuleErasedRecord> Records { get; init; }

    /// <summary>The salted hash of a key under this erasure's salt.</summary>
    public string HashOf(string key) => ErasureHash.Of(Salt, key);

    /// <summary>The salted hash of a key under a salt (R-CORE-131): what a tombstone keeps and an import compares.</summary>
    public static string KeyHash(string salt, string key) => ErasureHash.Of(salt, key);

    /// <summary>
    /// Whether it names this record (review F08, M2 and H3): one its pack carried (its own record, or a snapshot's it carried),
    /// of this type and key. Never by module: a definition pack's publisher cannot reach a host's or another publisher's records.
    /// </summary>
    public bool Names(Ingestion.ProposalSource? source, string type, string key) =>
        (source?.Pack?.Carrier?.Id ?? source?.Pack?.Id) == PackId && Erases(type, key);

    /// <summary>Whether it erases the record of this type and key.</summary>
    public bool Erases(string type, string key) =>
        Records.Any(r => r.Type == type && (r.Key == key || r.KeyHash is { } hash && hash == HashOf(key)));
}

/// <summary>What erasing did in a store: the records erased and the tombstones kept.</summary>
[Experimental("AMEXT001")]
public sealed record KnowledgePackEraseResult
{
    /// <summary>The pack.</summary>
    public required string PackId { get; init; }
    /// <summary>Distinct records whose every version was removed.</summary>
    public int Erased { get; init; }
    /// <summary>Tombstones written, new or already kept.</summary>
    public int Tombstones { get; init; }
}
