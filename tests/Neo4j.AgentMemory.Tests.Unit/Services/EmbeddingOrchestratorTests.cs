using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Core.Services;
using Neo4j.AgentMemory.Tests.Unit.TestHelpers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="EmbeddingOrchestrator"/>:
/// null/empty input handling, text composition, error resilience.
/// </summary>
public sealed class EmbeddingOrchestratorTests
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator =
        MockFactory.CreateStubEmbeddingGenerator(dimensions: 8);

    private EmbeddingOrchestrator CreateSut() =>
        new(_generator, NullLogger<EmbeddingOrchestrator>.Instance);

    // ── Null / empty / whitespace inputs ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task EmbedTextAsync_NullOrWhitespace_ReturnsEmptyArray(string? input)
    {
        var sut = CreateSut();
        var result = await sut.EmbedTextAsync(input!);
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmbedEntityAsync_NullOrWhitespace_ReturnsEmptyArray(string? input)
    {
        var sut = CreateSut();
        var result = await sut.EmbedEntityAsync(input!);
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmbedPreferenceAsync_NullOrWhitespace_ReturnsEmptyArray(string? input)
    {
        var sut = CreateSut();
        var result = await sut.EmbedPreferenceAsync(input!);
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmbedMessageAsync_NullOrWhitespace_ReturnsEmptyArray(string? input)
    {
        var sut = CreateSut();
        var result = await sut.EmbedMessageAsync(input!);
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmbedQueryAsync_NullOrWhitespace_ReturnsEmptyArray(string? input)
    {
        var sut = CreateSut();
        var result = await sut.EmbedQueryAsync(input!);
        result.Should().BeEmpty();
    }

    // ── Valid inputs produce non-empty embeddings ──

    [Fact]
    public async Task EmbedEntityAsync_ValidName_ReturnsNonEmptyVector()
    {
        var sut = CreateSut();
        var result = await sut.EmbedEntityAsync("Alice");
        result.Should().NotBeEmpty();
        result.Should().HaveCount(8);
    }

    [Fact]
    public async Task EmbedFactAsync_ValidTriple_ReturnsNonEmptyVector()
    {
        var sut = CreateSut();
        var result = await sut.EmbedFactAsync("Alice", "works_at", "Acme");
        result.Should().NotBeEmpty();
    }

    [Fact]
    public async Task EmbedPreferenceAsync_ValidText_ReturnsNonEmptyVector()
    {
        var sut = CreateSut();
        var result = await sut.EmbedPreferenceAsync("prefers dark mode");
        result.Should().NotBeEmpty();
    }

    // ── Text composition verification ──

    [Fact]
    public async Task EmbedFactAsync_ComposesTextAsSubjectPredicateObject()
    {
        // We use a fresh generator that captures the input text
        var capturedTexts = new List<string>();
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var texts = call.Arg<IEnumerable<string>>().ToList();
                capturedTexts.AddRange(texts);
                return MockFactory.EmbeddingResult(new float[] { 1f, 2f, 3f });
            });

        var sut = new EmbeddingOrchestrator(generator, NullLogger<EmbeddingOrchestrator>.Instance);
        await sut.EmbedFactAsync("Alice", "works_at", "Acme");

        capturedTexts.Should().ContainSingle()
            .Which.Should().Be("Alice works_at Acme");
    }

    [Fact]
    public async Task EmbedEntityAsync_PassesEntityNameDirectly()
    {
        var capturedTexts = new List<string>();
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedTexts.AddRange(call.Arg<IEnumerable<string>>());
                return MockFactory.EmbeddingResult(new float[] { 1f });
            });

        var sut = new EmbeddingOrchestrator(generator, NullLogger<EmbeddingOrchestrator>.Instance);
        await sut.EmbedEntityAsync("Alice");

        capturedTexts.Should().ContainSingle()
            .Which.Should().Be("Alice");
    }

    // ── Error handling ──

    [Fact]
    public async Task EmbedTextAsync_GeneratorThrows_ReturnsEmptyArrayGracefully()
    {
        var failingGenerator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        failingGenerator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("API unavailable"));

        var sut = new EmbeddingOrchestrator(failingGenerator, NullLogger<EmbeddingOrchestrator>.Instance);
        var result = await sut.EmbedTextAsync("some text");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task EmbedEntityAsync_GeneratorThrows_ReturnsEmptyArrayGracefully()
    {
        var failingGenerator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        failingGenerator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("network timeout"));

        var sut = new EmbeddingOrchestrator(failingGenerator, NullLogger<EmbeddingOrchestrator>.Instance);
        var result = await sut.EmbedEntityAsync("Alice");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task EmbedFactAsync_GeneratorThrows_ReturnsEmptyArrayGracefully()
    {
        var failingGenerator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        failingGenerator
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("server error"));

        var sut = new EmbeddingOrchestrator(failingGenerator, NullLogger<EmbeddingOrchestrator>.Instance);
        var result = await sut.EmbedFactAsync("Alice", "works_at", "Acme");

        result.Should().BeEmpty();
    }

    // ── Null handling for EmbedFactAsync parts ──

    [Fact]
    public async Task EmbedFactAsync_AllPartsNull_ReturnsEmptyArray()
    {
        var sut = CreateSut();
        // Concatenation of nulls → "  " (whitespace only) → empty guard
        var result = await sut.EmbedFactAsync(null!, null!, null!);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task EmbedFactAsync_AllPartsEmpty_ReturnsEmptyArray()
    {
        var sut = CreateSut();
        var result = await sut.EmbedFactAsync("", "", "");
        result.Should().BeEmpty();
    }
}
