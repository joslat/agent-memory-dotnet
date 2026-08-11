using AgentMemory.Abstractions.Domain;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// The sampling seed must reach the provider, and must be absent unless configured.
/// </summary>
/// <remarks>
/// <para>
/// Extraction asks for <c>Temperature = 0</c>, but some deployments reject an explicit zero and the
/// request is dropped, leaving the provider default of 1.0. Measured consequence: three cold builds of
/// one configuration stored 6,078 / 6,199 / 6,272 canonical triples with only <b>7.5%</b> common to
/// all three.
/// </para>
/// <para>
/// A seed is the other lever the API offers, and it <b>helps without solving it</b>: measured
/// self-agreement of one extractor with itself over identical input rose from <b>0.191 / 0.199</b>
/// unseeded to <b>0.309 / 0.340</b> seeded — non-overlapping, but nowhere near the 1.0 that
/// reproducibility would mean. The provider only promises determinism when its
/// <c>system_fingerprint</c> is unchanged too.
/// </para>
/// </remarks>
public sealed class ExtractionSeedTests
{
    private const string Response = """{"entities":[{"name":"Alice","type":"PERSON","confidence":0.9}]}""";

    private static (LlmEntityExtractor Extractor, IChatClient Client) Create(int? seed)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Response))));

        var options = new LlmExtractionOptions { Seed = seed };
        return (new LlmEntityExtractor(client, Options.Create(options),
            NullLogger<LlmEntityExtractor>.Instance), client);
    }

    private static IReadOnlyList<Message> Conversation() =>
    [
        new Message
        {
            MessageId = "m1",
            ConversationId = "c1",
            SessionId = "s1",
            Role = "user",
            Content = "Alice works at Contoso.",
            TimestampUtc = DateTimeOffset.UnixEpoch,
        },
    ];

    [Fact]
    public async Task AConfiguredSeedReachesTheProvider()
    {
        var (extractor, client) = Create(seed: 4242);

        await extractor.ExtractAsync(Conversation());

        await client.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions>(o => o.Seed == 4242),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoSeedIsSentWhenNoneIsConfigured()
    {
        // The byte-identical guarantee. Every measurement in the plan predates this option, so the
        // default must produce exactly the request it produced before — sending a seed of 0, or any
        // default value, would silently change what those runs would reproduce.
        var (extractor, client) = Create(seed: null);

        await extractor.ExtractAsync(Conversation());

        await client.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions>(o => o.Seed == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheSeedDefaultsToNull()
    {
        new LlmExtractionOptions().Seed.Should().BeNull();
    }
}
