using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// K-9 (database round trips, before DRT-8): a message embedded alone (EmbedMessageAsync, the per-message save) and in
/// a batch (EmbedBatchAsync, a turn's messages in one call) reaches the generator as the same input and is stored with
/// the same vector, so moving a turn to one call changes no stored embedding.
/// </summary>
public sealed class SingleAndBatchEmbeddingTests
{
    /// <summary>A deterministic generator that records every input it is asked to embed.</summary>
    private sealed class Recording : IEmbeddingGenerator<string, Embedding<float>>
    {
        public readonly List<string> Inputs = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            lock (Inputs) Inputs.AddRange(list);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list.Select(v => new Embedding<float>(Vector(v)))));
        }

        private static float[] Vector(string text)
        {
            var vector = new float[8];
            foreach (var c in text) vector[c % 8] += 1;
            return vector;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static readonly string[] Texts =
    [
        "I prefer window seats.",
        "Got it. Window seats from now on.",
        "  spaces around  ",
        "Zürich ✓ and an emoji 🙂",
        new string('x', 40_000), // past the input cap: both paths cap it the same way
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public async Task A_message_alone_and_in_a_batch_gets_the_same_vector_from_the_same_input(int cacheEntries)
    {
        var single = new Recording();
        var batch = new Recording();
        var one = new EmbeddingOrchestrator(single, NullLogger<EmbeddingOrchestrator>.Instance, new EmbeddingVectorCache(cacheEntries));
        var many = new EmbeddingOrchestrator(batch, NullLogger<EmbeddingOrchestrator>.Instance, new EmbeddingVectorCache(cacheEntries));

        var alone = new List<float[]>();
        foreach (var text in Texts) alone.Add(await one.EmbedMessageAsync(text));
        var together = await many.EmbedBatchAsync(Texts);

        together.Should().HaveCount(Texts.Length);
        for (var i = 0; i < Texts.Length; i++)
            together[i].Should().Equal(alone[i], $"text {i} must be embedded identically alone and in a batch");
        batch.Inputs.Should().Equal(single.Inputs, "the generator sees the same inputs, capped the same way");
    }

    [Fact]
    public async Task A_blank_message_gets_no_vector_either_way()
    {
        var generator = new Recording();
        var sut = new EmbeddingOrchestrator(generator, NullLogger<EmbeddingOrchestrator>.Instance);

        (await sut.EmbedMessageAsync("   ")).Should().BeEmpty();
        (await sut.EmbedBatchAsync(["   ", "words"]))[0].Should().BeEmpty();
        generator.Inputs.Should().Equal("words");
    }
}
