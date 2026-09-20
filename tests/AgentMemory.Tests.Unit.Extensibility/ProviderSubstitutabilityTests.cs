using System.Reflection;
using AgentMemory.AgentFramework;
using AgentMemory.Extensibility.AgentFramework;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The extensible provider must be constructible as everything the shipped one is.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this catches does not fail to compile and does not fail a hand-built parity test.</b>
/// The shipped provider takes five OPTIONAL dependencies — store context, owner context, tool
/// factory, recall policy, admission policy — and the first version of the derived provider passed
/// none of them to <c>base</c>. Everything built, every existing test passed, and a host that had
/// configured any of them would have silently lost it: a multi-tenant deployment on the wrong store,
/// an agent with no memory tools, and core memory with the #92 admission gate removed — from the
/// class whose entire claim is that it preserves the shipped provider's behaviour.
/// </para>
/// <para>
/// The parity tests could not see it because they construct both providers by hand with the same
/// arguments. This test compares the two constructors instead, so it also catches the FUTURE case:
/// the base gains a dependency, the derived one does not, and nothing else notices.
/// </para>
/// </remarks>
public sealed class ProviderSubstitutabilityTests
{
    private static ConstructorInfo Longest(Type type) =>
        type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();

    /// <summary>Every dependency type the base accepts, the derived one accepts too.</summary>
    [Fact]
    public void TheDerivedProviderAcceptsEveryDependencyTheBaseDoes()
    {
        var baseParameters = Longest(typeof(Neo4jMemoryContextProvider))
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToHashSet();

        var derivedParameters = Longest(typeof(ExtensibleMemoryContextProvider))
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToHashSet();

        var dropped = baseParameters
            .Where(t => !derivedParameters.Contains(t))
            // The base logger is the one deliberate substitution: the derived provider takes both
            // its own logger and the base's, and passes the base's through.
            .Where(t => t != typeof(Microsoft.Extensions.Logging.ILogger<Neo4jMemoryContextProvider>)
                        || !derivedParameters.Contains(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        dropped.Should().BeEmpty(
            "a dependency the derived provider cannot accept is one a configured host silently loses");
    }

    /// <summary>
    /// The optional ones stay optional, so existing construction sites keep working.
    /// </summary>
    [Fact]
    public void TheAddedDependenciesAreOptional()
    {
        var required = Longest(typeof(ExtensibleMemoryContextProvider))
            .GetParameters()
            .Count(p => !p.HasDefaultValue);

        var baseRequired = Longest(typeof(Neo4jMemoryContextProvider))
            .GetParameters()
            .Count(p => !p.HasDefaultValue);

        // The derived provider adds three of its own required dependencies: the compiler, the module
        // contributors and its logger. Anything beyond that would be a new burden on every caller.
        required.Should().Be(baseRequired + 3);
    }
}
