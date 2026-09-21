using System.Reflection;
using System.Runtime.CompilerServices;
using AgentMemory.Extensibility.Context;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The SDK's dependency rule, asserted rather than intended.
/// </summary>
/// <remarks>
/// <para>
/// <b>Abstractions only.</b> Nothing the SDK needs lives in Core — the compiler talks to
/// <c>IMemoryContextAssembler</c>, an Abstractions interface, and <c>ChatMessage</c> comes from
/// <c>Microsoft.Extensions.AI.Abstractions</c>, which Abstractions already references. Keeping it
/// that way is what lets a host on a different storage backend consume the SDK without dragging the
/// Neo4j-backed Core in behind it.
/// </para>
/// <para>
/// Asserted here rather than in <c>eng/package-consumers</c>, which are release smoke consumers that
/// prove packages install — a different question from what an assembly is allowed to reference.
/// </para>
/// </remarks>
public sealed class ArchitectureTests
{
    private static readonly Assembly Sdk = typeof(ContextCompiler).Assembly;

    /// <summary>The SDK must not reference Core, directly or by any other AgentMemory assembly.</summary>
    [Fact]
    public void TheSdkReferencesAbstractionsAndNothingElseFromAgentMemory()
    {
        var agentMemoryReferences = Sdk.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("AgentMemory", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        agentMemoryReferences.Should().BeEquivalentTo(
            ["AgentMemory.Abstractions"],
            "an Abstractions-only SDK is what lets a host without Core consume it");
    }

    /// <summary>
    /// Every public type carries the experimental marker.
    /// </summary>
    /// <remarks>
    /// The 0.x version says "not settled" to whoever reads the package listing; the attribute says it
    /// at the call site, where the decision to depend on a type is actually made. A public type that
    /// escaped the marker would look as stable as the 1.5.0 core, which is the confusion the two
    /// signals exist together to prevent.
    /// </remarks>
    [Fact]
    public void EveryPublicSdkTypeIsMarkedExperimental()
    {
        var unmarked = Sdk.GetExportedTypes()
            .Where(type => type.GetCustomAttribute<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>() is null)
            // Compiler-generated helpers are not part of the surface anyone depends on.
            .Where(type => type.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
            .Select(type => type.FullName ?? type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        unmarked.Should().BeEmpty("the 0.x line and the attribute must agree");
    }
}
