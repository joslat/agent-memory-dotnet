using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// The opt-in LLM deriver (30.10, design step 6).
/// </summary>
/// <remarks>
/// Every test here is about a failure mode, because the contract is that <b>none of them throw</b>.
/// A derivation failure must degrade to "no fan-out this turn" — the recall the caller actually asked
/// for still has to happen. A silent fallback to the deterministic deriver would be worse than no
/// fan-out at all: the witness would then name a deriver that never ran.
/// </remarks>
public sealed class LlmSubQueryDeriverTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();

    private LlmSubQueryDeriver CreateSut() =>
        new(_chatClient, NullLogger<LlmSubQueryDeriver>.Instance);

    private void RespondWith(string text) =>
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    [Fact]
    public void TheDeriverIdIsStable() => CreateSut().DeriverId.Should().Be("llm-v1");

    [Fact]
    public async Task ValidJsonIsParsedIntoTypedLegs()
    {
        RespondWith("""
            [{"affinity":"Temporal","text":"when did I join Acme"},
             {"affinity":"Preference","text":"what do I like for lunch"}]
            """);

        var legs = await CreateSut().DeriveAsync("q", 4, CancellationToken.None);

        legs.Should().HaveCount(2);
        legs[0].Affinity.Should().Be(MemoryTypeAffinity.Temporal);
        legs[1].Affinity.Should().Be(MemoryTypeAffinity.Preference);
    }

    [Fact]
    public async Task TheCapIsAppliedToTheModelsOutput()
    {
        // The model does not get to decide how many round trips this costs.
        RespondWith("""
            [{"affinity":"Semantic","text":"a"},{"affinity":"Semantic","text":"b"},
             {"affinity":"Semantic","text":"c"},{"affinity":"Semantic","text":"d"}]
            """);

        (await CreateSut().DeriveAsync("q", 2, CancellationToken.None)).Should().HaveCount(2);
    }

    [Fact]
    public async Task AnUnknownAffinityIsDroppedRatherThanCoerced()
    {
        // Coercing to Semantic would route a leg to a store the model did not choose, and then report
        // it as though the model had chosen it.
        RespondWith("""
            [{"affinity":"Telepathic","text":"a"},{"affinity":"Semantic","text":"b"}]
            """);

        var legs = await CreateSut().DeriveAsync("q", 4, CancellationToken.None);

        legs.Should().ContainSingle();
        legs[0].Affinity.Should().Be(MemoryTypeAffinity.Semantic);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"affinity\":\"Semantic\"}")]          // an object, not an array
    [InlineData("[\"just\",\"strings\"]")]
    [InlineData("[{\"text\":\"missing affinity\"}]")]
    [InlineData("[{\"affinity\":\"Semantic\"}]")]        // missing text
    [InlineData("")]
    public async Task GarbageYieldsNoLegsAndNeverThrows(string response)
    {
        RespondWith(response);

        var derive = async () => await CreateSut().DeriveAsync("q", 4, CancellationToken.None);

        (await derive.Should().NotThrowAsync()).Which.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Semantic,Temporal")]
    [InlineData("Temporal, Episodic")]
    public async Task ACommaSeparatedAffinityIsDropped_NotCoercedIntoAThirdOne(string affinity)
    {
        // R5. Enum.TryParse bitwise-ORs a comma list even for a non-[Flags] enum: "Semantic,Temporal"
        // is 1|2 = 3 = Episodic, and IsDefined then passes it. A model hedging between two affinities
        // was silently routed to a definite third store it never named.
        RespondWith("[{\"affinity\":\"" + affinity + "\",\"text\":\"a\"}]");

        var legs = await CreateSut().DeriveAsync("q", 4, CancellationToken.None);

        legs.Should().BeEmpty("there is no correct way to guess which half of a hedge was meant");
    }

    [Fact]
    public async Task AProviderExceptionYieldsNoLegsAndNeverThrows()
    {
        // The recall the caller asked for must still happen. A fan-out is an enhancement; it cannot
        // take the primary path down with it.
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("provider is having a day"));

        var derive = async () => await CreateSut().DeriveAsync("q", 4, CancellationToken.None);

        (await derive.Should().NotThrowAsync()).Which.Should().BeEmpty();
    }

    [Fact]
    public async Task CancellationIsPropagated_NotSwallowedAsAFailure()
    {
        // A cancelled recall is not a failed derivation. Swallowing it here would report
        // "derivation-failed" for a turn the caller deliberately abandoned.
        _chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var derive = async () => await CreateSut().DeriveAsync("q", 4, cancelled.Token);

        await derive.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AnEmptyArrayIsALegitimateAnswer()
    {
        // "This does not decompose" is a real answer, not an error.
        RespondWith("[]");

        (await CreateSut().DeriveAsync("q", 4, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task NoModelCallIsMadeForAnEmptyQuery()
    {
        var legs = await CreateSut().DeriveAsync("   ", 4, CancellationToken.None);

        legs.Should().BeEmpty();
        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
