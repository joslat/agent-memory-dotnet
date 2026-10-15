using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Modules;

/// <summary>Read selection over always-versioned module records; this never changes write behavior.</summary>
[Experimental("AMEXT001")]
public enum ModuleRecordSelection
{
    /// <summary>Current or system-time live versions, using half-open creation/invalidation intervals.</summary>
    Snapshot,
    /// <summary>Retained versions of one exact key, newest first, with an optional creation-time cutoff.</summary>
    History
}
