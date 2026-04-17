using Microsoft.Extensions.AI;
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

    public static IEmbeddingGenerator<string, Embedding<float>> CreateStubEmbeddingGenerator(int dimensions = 1536)
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        generator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var texts = call.Arg<IEnumerable<string>>();
                var embeddings = new GeneratedEmbeddings<Embedding<float>>(
                    texts.Select(t => new Embedding<float>(BuildDeterministicVector(t, dimensions))).ToList());
                return Task.FromResult(embeddings);
            });

        return generator;
    }

    /// <summary>
    /// Creates a GeneratedEmbeddings result wrapping a single float[] vector.
    /// Use with NSubstitute: .Returns(call => MockFactory.EmbeddingResult(someVector))
    /// </summary>
    public static Task<GeneratedEmbeddings<Embedding<float>>> EmbeddingResult(float[] vector) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(vector)]));

    /// <summary>
    /// Creates a GeneratedEmbeddings result returning the same vector for every input.
    /// Use with NSubstitute: .Returns(call => MockFactory.EmbeddingResult(call, someVector))
    /// </summary>
    public static Task<GeneratedEmbeddings<Embedding<float>>> EmbeddingResult(
        NSubstitute.Core.CallInfo call, float[] vector)
    {
        var texts = call.Arg<IEnumerable<string>>();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            texts.Select(_ => new Embedding<float>(vector)).ToList()));
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
