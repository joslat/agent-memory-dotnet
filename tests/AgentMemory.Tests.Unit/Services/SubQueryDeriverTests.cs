using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Services;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// The deterministic sub-query deriver (30.10, design step 5).
/// </summary>
/// <remarks>
/// Provider-free by construction: these run without a model, a container, or a network, which is the
/// property that makes this the default deriver. A mechanism whose legs depend on a sampled model
/// output cannot have its before/after measured twice and compared.
/// </remarks>
public sealed class SubQueryDeriverTests
{
    private readonly DeterministicSubQueryDeriver _deriver = new();

    private Task<IReadOnlyList<RecallSubQuery>> Derive(string query, int max = 4) =>
        _deriver.DeriveAsync(query, max, CancellationToken.None);

    [Fact]
    public void TheDeriverIdIsStable()
    {
        // Recorded in the witness; a run whose deriver cannot be named cannot be reproduced.
        _deriver.DeriverId.Should().Be("det-v1");
    }

    [Fact]
    public async Task TheConjunctionCaseYieldsTwoLegsWithDifferentTexts()
    {
        // The design's named example. Two legs that carry the SAME text would be two identical
        // retrievals billed as a fan-out.
        var legs = await Derive("What did I see at the MoMA and what did I order at the Met");

        legs.Should().HaveCount(2);
        legs[0].QueryText.Should().NotBe(legs[1].QueryText);
    }

    [Fact]
    public async Task ASingleUndecomposableQueryYieldsNothing()
    {
        // Returning the whole query as one "sub-query" would re-issue the monolithic query under
        // another name and bill an extra embedding for the privilege.
        (await Derive("what did i eat")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("what I said about the report and the Acme contract", MemoryTypeAffinity.Episodic)]
    [InlineData("how do I reset it and the Acme contract", MemoryTypeAffinity.Procedural)]
    [InlineData("the Acme contract in March and something else", MemoryTypeAffinity.Temporal)]
    [InlineData("what I prefer for lunch and the Acme contract", MemoryTypeAffinity.Preference)]
    public async Task EachSignalTypesItsOwnFragment(string query, MemoryTypeAffinity expected)
    {
        var legs = await Derive(query);

        legs.Should().NotBeEmpty();
        legs[0].Affinity.Should().Be(expected);
    }

    [Fact]
    public async Task SemanticIsTheResidual()
    {
        // Semantic has no positive keyword set on purpose: given one it would fire on nearly
        // everything, and the other four types are the discriminating ones.
        var legs = await Derive("the Acme contract and the Globex invoice");

        legs.Should().OnlyContain(leg => leg.Affinity == MemoryTypeAffinity.Semantic);
    }

    [Fact]
    public async Task EpisodicBeatsPreference_BecauseTheAskIsAboutTheConversation()
    {
        // "you mentioned I like X" asks what was SAID, not what is preferred. Preference-first would
        // route it to the wrong store and the leg would come back empty.
        var legs = await Derive("you mentioned I like sushi and the Acme contract");

        legs[0].Affinity.Should().Be(MemoryTypeAffinity.Episodic);
    }

    [Fact]
    public async Task ATemporalFragmentWithoutAnInterrogativeIsPrefixed()
    {
        // A bare fragment embeds as a statement; the retrieval it needs is a date question.
        var legs = await Derive("the Acme contract in March and the Globex invoice");

        legs[0].Affinity.Should().Be(MemoryTypeAffinity.Temporal);
        legs[0].QueryText.Should().StartWith("on what date ");
    }

    [Fact]
    public async Task ATemporalFragmentThatAlreadyAsksIsNotPrefixed()
    {
        var legs = await Derive("when did I sign the Acme contract and the Globex invoice");

        legs[0].QueryText.Should().NotStartWith("on what date ");
    }

    [Fact]
    public async Task TheCapIsHonoured()
    {
        var legs = await Derive("A and B and C and D and E and F", max: 2);

        legs.Should().HaveCount(2, "each leg costs an embedding and a retrieval round trip");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ANonPositiveCapYieldsNothing(int max)
    {
        (await Derive("the Acme contract and the Globex invoice", max)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyInputYieldsNothingAndNeverThrows(string query)
    {
        (await Derive(query)).Should().BeEmpty();
    }

    [Fact]
    public async Task DerivationIsDeterministic()
    {
        // The whole reason this is the default deriver.
        const string Query = "What did I see at the MoMA and what did I order at the Met";

        var first = await Derive(Query);
        var second = await Derive(Query);

        second.Select(l => (l.Affinity, l.QueryText))
            .Should().Equal(first.Select(l => (l.Affinity, l.QueryText)));
    }

    [Fact]
    public async Task NoLegCarriesAPreComputedEmbedding()
    {
        // The deriver does not embed; the assembler does. A deriver that embedded would need a
        // provider and would stop being free and provider-free.
        var legs = await Derive("the Acme contract and the Globex invoice");

        legs.Should().OnlyContain(leg => leg.QueryEmbedding == null);
    }
}
