using System.Reflection;
using AgentMemory.Abstractions.Domain;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.McpServer;

/// <summary>
/// The MCP wire projection (0.7).
/// </summary>
/// <remarks>
/// The tools serialized domain objects directly, and <c>Entity</c>, <c>Fact</c>, <c>Preference</c>
/// and <c>ReasoningTrace</c> all carry an embedding — so every recall put 384 or 1536 floats per item
/// on the wire. Nothing failed; the payload was just enormous, which is the kind of defect that never
/// gets noticed because it looks like working software.
/// </remarks>
public sealed class McpMemoryProjectionTests
{
    private static object Invoke(string method, object argument)
    {
        var type = typeof(AgentMemory.McpServer.ServiceCollectionExtensions).Assembly
            .GetType("AgentMemory.McpServer.Tools.McpMemoryProjection")!;
        return type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [argument])!;
    }

    private static IEnumerable<string> PropertyNames(object projected) =>
        projected.GetType().GetProperties().Select(property => property.Name);

    [Fact]
    public void NoProjectedShapeCarriesAnEmbedding()
    {
        // THE guard, asserted by name across every shape rather than one at a time: a category added
        // later and projected raw is the same bug wearing a different label.
        var now = DateTimeOffset.UtcNow;
        var vector = new float[1536];

        var shapes = new[]
        {
            Invoke("Entity", new Entity
            {
                EntityId = "e1", Name = "Jose", Type = "person", Confidence = 1, CreatedAtUtc = now,
                Embedding = vector,
            }),
            Invoke("Fact", new Fact
            {
                FactId = "f1", Subject = "jose", Predicate = "lives in", Object = "Zurich",
                Confidence = 1, CreatedAtUtc = now, Embedding = vector,
            }),
            Invoke("Preference", new Preference
            {
                PreferenceId = "p1", Category = "food", PreferenceText = "no coriander",
                Confidence = 1, CreatedAtUtc = now, Embedding = vector,
            }),
            Invoke("Trace", new ReasoningTrace
            {
                TraceId = "t1", SessionId = "s1", Task = "book a train", StartedAtUtc = now,
                TaskEmbedding = vector,
            }),
        };

        foreach (var shape in shapes)
        {
            PropertyNames(shape).Should().NotContain(
                name => name.Contains("mbedding", StringComparison.Ordinal),
                "no MCP payload should carry a stored vector: {0}", shape.GetType().Name);
        }
    }

    [Fact]
    public void ATraceCarriesItsOutcomeNotOnlyItsTask()
    {
        // A recalled procedure rendering what was attempted and dropping how it went says "you have
        // done this before" and nothing about how -- the exact product gap 7.6 spent five runs finding.
        var projected = Invoke("Trace", new ReasoningTrace
        {
            TraceId = "t1", SessionId = "s1", Task = "book a train",
            Outcome = "refresh the session first, then place the hold",
            Success = true, Kind = TraceKind.Procedure, StartedAtUtc = DateTimeOffset.UtcNow,
        });

        var names = PropertyNames(projected).ToList();
        names.Should().Contain("Outcome");
        names.Should().Contain("Task");
        names.Should().Contain("Kind", "a client cannot tell a procedure from an episode without it");
    }

    [Fact]
    public void TheProjectedFactStillCarriesWhatAClientNeeds()
    {
        // Dropping the vector must not drop the payload. Provenance and validity are what make a fact
        // checkable, so their loss would be a quieter regression than the one being fixed.
        var projected = Invoke("Fact", new Fact
        {
            FactId = "f1", Subject = "jose", Predicate = "lives in", Object = "Zurich",
            Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow,
            SourceMessageIds = ["m1"], Embedding = new float[384],
        });

        var names = PropertyNames(projected).ToList();
        names.Should().Contain(["Subject", "Predicate", "Object", "Confidence", "SourceMessageIds"]);
    }
}
