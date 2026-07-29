using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalRuntimeTests
{
    [Fact]
    public async Task CreateCompatibleChatClient_NormalizesOnlyTheExactAgentEvalJudgeOptions()
    {
        var seen = new List<(float? Temperature, int? MaxOutputTokens)>();
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var options = call.Arg<ChatOptions?>();
                seen.Add((options?.Temperature, options?.MaxOutputTokens));
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
            });
        var client = LongMemEvalRuntime.CreateCompatibleChatClient(inner);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "AgentEval judge")],
            new ChatOptions { Temperature = 0, MaxOutputTokens = 30 });
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "non-judge request")],
            new ChatOptions { Temperature = 0.25f, MaxOutputTokens = 30 });
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "different zero-temperature request")],
            new ChatOptions { Temperature = 0, MaxOutputTokens = 128 });

        seen.Should().Equal(
            (null, 512),
            (0.25f, 30),
            (0f, 128));
    }
    [Fact]
    public async Task ProbeEmbeddingDimensionsAsync_ReturnsRealProviderVectorLength()
    {
        var generator = new FixedEmbeddingGenerator(1536);

        var dimensions = await LongMemEvalRuntime.ProbeEmbeddingDimensionsAsync(generator);

        dimensions.Should().Be(1536);
        generator.Inputs.Should().Equal("AgentMemory LongMemEval embedding dimension probe");
    }

    [Fact]
    public async Task ProbeEmbeddingDimensionsAsync_RejectsAnEmptyProviderResponse()
    {
        var generator = new EmptyEmbeddingGenerator();

        Func<Task> act = async () => await LongMemEvalRuntime.ProbeEmbeddingDimensionsAsync(generator);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*embedding*");
    }

    [Fact]
    public async Task ExecuteStageAsync_SanitizesProviderFailure()
    {
        Func<Task> act = async () => await LongMemEvalRuntime.ExecuteStageAsync(
            "storage",
            () => Task.FromException<int>(new InvalidOperationException("provider-secret-detail")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("LongMemEval storage stage failed.");
    }

    private sealed class FixedEmbeddingGenerator(int dimensions)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<string> Inputs { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Inputs.AddRange(values);
            return Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(
                [new Embedding<float>(new float[dimensions])]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class EmptyEmbeddingGenerator
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>());

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
