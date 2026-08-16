using System.Reflection;
using AgentMemory.AgentFramework;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// The <c>AgentFrameworkOptions.ContextFormat</c> → <c>ContextFormatOptions</c> bridge (0.4).
/// </summary>
/// <remarks>
/// <para>
/// The bridge copies properties one by one, so a property added to <c>ContextFormatOptions</c> and
/// forgotten here binds successfully, validates successfully, and does nothing. That is exactly what
/// happened to <c>IncludeTraceOutcomes</c>: the documented procedural-memory recipe was inert from
/// the day it was written, because a host that set the flag got a recalled procedure rendering its
/// task and dropping its outcome.
/// </para>
/// <para>
/// So this file has two kinds of test. The behavioural one proves the specific property now arrives;
/// the reflection one proves the <i>eleventh</i> property cannot repeat the mistake, because a copy
/// loop is a list that drifts and a list is not a contract.
/// </para>
/// </remarks>
public sealed class ContextFormatOptionsBridgeTests
{
    private static ContextFormatOptions Resolve(Action<AgentFrameworkOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddOptions<AgentFrameworkOptions>().Configure(configure);
        // Re-register the bridge exactly as AddAgentMemoryFramework does, without dragging the whole
        // Neo4j graph in: the mapping is the unit under test, not the provider it usually ships with.
        services.AddOptions<ContextFormatOptions>()
            .Configure<IOptions<AgentFrameworkOptions>>((ctx, af) => Copy(af.Value.ContextFormat, ctx));
        return services.BuildServiceProvider().GetRequiredService<IOptions<ContextFormatOptions>>().Value;
    }

    private static void Copy(ContextFormatOptions src, ContextFormatOptions ctx)
    {
        foreach (var property in Settable())
            property.SetValue(ctx, property.GetValue(src));
    }

    private static IEnumerable<PropertyInfo> Settable() =>
        typeof(ContextFormatOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite);

    [Fact]
    public void IncludeTraceOutcomesReachesTheFormatter()
    {
        // Red before 0.4. The property bound, validated, and was dropped by the copy loop -- so a
        // recalled procedure said "you have done this before" and nothing about how.
        var options = ProductionBridge(af => af.ContextFormat.IncludeTraceOutcomes = true);

        options.IncludeTraceOutcomes.Should().BeTrue();
    }

    [Fact]
    public void EverySettablePropertyCrossesTheBridge()
    {
        // THE guard. Each property is given a value distinguishable from its default, and the bridged
        // result must carry all of them. A property added later and forgotten fails here rather than
        // in a benchmark six weeks on.
        var source = new ContextFormatOptions();
        var mutated = new List<PropertyInfo>();
        foreach (var property in Settable())
        {
            if (Distinct(property, property.GetValue(source)) is not { } value) continue;
            property.SetValue(source, value);
            mutated.Add(property);
        }

        mutated.Should().NotBeEmpty("the fixture must actually change something");

        var bridged = ProductionBridge(af =>
        {
            foreach (var property in mutated)
                property.SetValue(af.ContextFormat, property.GetValue(source));
        });

        foreach (var property in mutated)
        {
            property.GetValue(bridged).Should().Be(
                property.GetValue(source),
                "ContextFormatOptions.{0} must cross the AgentFrameworkOptions bridge", property.Name);
        }
    }

    /// <summary>
    /// Resolves through the SHIPPED registration, not the local copy above.
    /// </summary>
    /// <remarks>
    /// A test that re-implements the mapping it is checking passes against its own reimplementation.
    /// This one goes through <c>AddAgentMemoryFramework</c>'s own configuration so the assertion binds
    /// to production code.
    /// </remarks>
    private static ContextFormatOptions ProductionBridge(Action<AgentFrameworkOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryFramework(configure);
        return services.BuildServiceProvider().GetRequiredService<IOptions<ContextFormatOptions>>().Value;
    }

    private static object? Distinct(PropertyInfo property, object? current)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(bool)) return !(bool)(current ?? false);
        if (type == typeof(int)) return (int)(current ?? 0) + 7;
        if (type == typeof(string)) return $"{current}-bridged";
        if (type.IsEnum)
        {
            // Any member other than the current one; a same-value "change" would let a dropped
            // property pass by coincidence.
            return Enum.GetValues(type).Cast<object>()
                .FirstOrDefault(candidate => !Equals(candidate, current));
        }

        return null;
    }
}
