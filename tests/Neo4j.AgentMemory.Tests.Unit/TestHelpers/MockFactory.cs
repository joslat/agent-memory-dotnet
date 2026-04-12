using Neo4j.AgentMemory.Abstractions.Services;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.TestHelpers;

public static class MockFactory
{
    public static IClock CreateFixedClock(DateTimeOffset? fixedTime = null)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(fixedTime ?? new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return clock;
    }

    public static IIdGenerator CreateSequentialIdGenerator(string prefix = "test")
    {
        var count = 0;
        var generator = Substitute.For<IIdGenerator>();
        generator.GenerateId().Returns(_ => $"{prefix}-{++count}");
        return generator;
    }

    public static IEmbeddingProvider CreateStubEmbeddingProvider(int dimensions = 1536)
    {
        var provider = Substitute.For<IEmbeddingProvider>();
        provider.EmbeddingDimensions.Returns(dimensions);

        provider
            .GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var text = call.Arg<string>();
                return Task.FromResult(BuildDeterministicVector(text, dimensions));
            });

        provider
            .GenerateEmbeddingsAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var texts = call.Arg<IEnumerable<string>>();
                IReadOnlyList<float[]> vectors = texts
                    .Select(t => BuildDeterministicVector(t, dimensions))
                    .ToList();
                return Task.FromResult(vectors);
            });

        return provider;
    }

    private static float[] BuildDeterministicVector(string text, int dimensions)
    {
        var vector = new float[dimensions];
        var rng = new Random(text.GetHashCode());
        for (var i = 0; i < dimensions; i++)
            vector[i] = (float)rng.NextDouble();
        return vector;
    }
}
