using System.Text.RegularExpressions;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// <c>--extraction-reasoning</c> reaches the extraction request, names itself in the corpus identity,
/// and changes nothing when it is absent.
/// </summary>
/// <remarks>
/// The 2026-09-29 Bitdeer checkpoint sent no reasoning setting. GLM-5.3-Flash then spent about 55–60% of
/// its output tokens reasoning, at a median of 27 s per extraction call; at effort Low, set on the
/// extraction client as this is, the same model measured 7.8 s.
/// </remarks>
public sealed class ExtractionReasoningTests
{
    [Theory]
    [InlineData("low", ReasoningEffort.Low)]
    [InlineData("MEDIUM", ReasoningEffort.Medium)]
    [InlineData("high", ReasoningEffort.High)]
    public void TheEffortParses(string value, ReasoningEffort expected) =>
        LongMemEvalExtractionReasoning.Parse(value).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("default")]
    public void DefaultMeansSendNothing(string? value) =>
        LongMemEvalExtractionReasoning.Parse(value).Should().BeNull();

    [Theory]
    [InlineData("none")]
    [InlineData("minimal")]
    public void AnythingElseIsRefused(string value)
    {
        var act = () => LongMemEvalExtractionReasoning.Parse(value);

        act.Should().Throw<ArgumentException>().WithMessage("*--extraction-reasoning*");
    }

    [Fact]
    public void TheRequestedEffortReachesTheExtractionRequest()
    {
        var inner = new RecordingChatClient();

        LongMemEvalExtractionReasoning.Apply(inner, ReasoningEffort.Low)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "extract")], new ChatOptions()).GetAwaiter().GetResult();

        inner.Options!.Reasoning!.Effort.Should().Be(ReasoningEffort.Low);
    }

    [Fact]
    public void WithoutAnEffortTheClientIsReturnedUntouched()
    {
        var inner = new RecordingChatClient();

        LongMemEvalExtractionReasoning.Apply(inner, null).Should().BeSameAs(inner);
    }

    [Fact]
    public void TheEffortIsPartOfTheCorpusIdentityOnlyWhenRequested()
    {
        LongMemEvalExtractionReasoning.Identity("zai-org/GLM-5.3-Flash@bitdeer", ReasoningEffort.Low)
            .Should().Be("zai-org/GLM-5.3-Flash@bitdeer+reasoning-low");
        LongMemEvalExtractionReasoning.Identity("zai-org/GLM-5.3-Flash@bitdeer", null)
            .Should().Be("zai-org/GLM-5.3-Flash@bitdeer");
        LongMemEvalExtractionReasoning.Token(null).Should().Be("provider-default");
    }

    [Fact]
    public void ThePreparedPairParsesTheFlag()
    {
        LongMemEvalPreparedPairProgram.Parse(["--prepared-pair", "--extraction-reasoning", "low"])
            .ExtractionReasoning.Should().Be(ReasoningEffort.Low);
        LongMemEvalPreparedPairProgram.Parse(["--prepared-pair"]).ExtractionReasoning.Should().BeNull();
    }

    /// <summary>
    /// Every extraction client the prepared pair builds carries the effort, and every identity it seals
    /// or compares includes it.
    /// </summary>
    [Fact]
    public void EveryExtractionClientAndIdentityInThePreparedPairCarriesIt()
    {
        var source = string.Join('\n', ToolSource("LongMemEvalPreparedPairProgram.cs")
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Regex.Matches(source, @"new ProviderCompatibleExtractionChatClient\(\s*model\.CreateExtractionClient\(\)")
            .Should().BeEmpty("a bare extraction client would ignore the requested effort");
        Regex.Matches(source, @"LongMemEvalExtractionReasoning\.Apply\(").Count.Should().Be(2);
        Regex.Matches(source, @"model\.ExtractionIdentity\b(?!, options\.ExtractionReasoning)")
            .Should().BeEmpty("the identity is read once, with the effort folded in");
    }

    private static string ToolSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(
            directory!.FullName, "tools", "AgentMemory.LongMemEval", fileName));
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
