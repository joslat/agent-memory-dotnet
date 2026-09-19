using AgentMemory.Abstractions.Domain;
using AgentMemory.Extraction.Llm;
using AgentMemory.Extraction.Llm.Internal;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// E-1. An alias survives only if the conversation DECLARED it.
/// </summary>
/// <remarks>
/// <para>
/// Written after the stage-1 probe, not before it. The probe captured two aliases: "Calderwick
/// office" = "Head office", declared outright in a turn and exactly what this feature is for; and
/// "Thorne Bramkjar" = "Bram Thornevund", two people whose names share components and who never once
/// appear in the same sentence anywhere in the corpus. The model invented the second from
/// resemblance, which the prompt forbids in as many words.
/// </para>
/// <para>
/// So the prompt cannot be where this is enforced — it was explicit and was overridden, in the same
/// shape as the anchored-validity failure. Co-occurrence in one turn is checkable in code, which
/// turns "the conversation said so" from a promise about the model into a property of the text.
/// </para>
/// </remarks>
public sealed class AliasEvidenceTests
{
    private static ExtractionRequest Session(params string[] turns) => new()
    {
        SessionId = "s1",
        UserId = "owner-1",
        Messages = turns.Select((text, i) => new Message
        {
            MessageId = $"m{i}",
            SessionId = "s1",
            ConversationId = "c1",
            Role = "user",
            Content = text,
            TimestampUtc = DateTimeOffset.UnixEpoch.AddMinutes(i),
        }).ToArray(),
    };

    private static IReadOnlyList<string> Aliases(ExtractionRequest session, string name, params string[] aliases)
    {
        var response = new LlmExtractionResponse
        {
            ProcessedSourceSessions = ["s1"],
            Entities =
            [
                new LlmEntityDto
                {
                    SourceSession = "s1",
                    Name = name,
                    Type = "LOCATION",
                    Confidence = 0.9,
                    Aliases = aliases.ToList(),
                },
            ],
        };

        var projected = LlmMultiSessionUnifiedMemoryExtractor.ProjectAndValidate(
            response, [session]);

        return projected["s1"].Entities.Single().Aliases;
    }

    /// <summary>A declared identity survives — the case the feature exists for.</summary>
    [Fact]
    public void AnAliasDeclaredInATurnIsKept()
    {
        var session = Session("Head office is the Calderwick office, for the record.");

        Aliases(session, "Head office", "Calderwick office")
            .Should().ContainSingle().Which.Should().Be("Calderwick office");
    }

    /// <summary>
    /// An alias the conversation never states is dropped, however alike the names look.
    /// </summary>
    /// <remarks>
    /// The measured case, verbatim: the corpus builds person names from a shared component pool, so
    /// resemblance is everywhere and a model asked to spot identities will find them. Two distinct
    /// referents recorded as one cannot be separated again, and it inflates exactly the counting
    /// questions this feature is measured on — the Goodhart failure registered in advance.
    /// </remarks>
    [Fact]
    public void AnAliasInventedFromResemblanceIsDropped()
    {
        var session = Session(
            "Thorne Bramkjar said as much.",
            "Bram Thornevund mentioned it later, separately.");

        Aliases(session, "Thorne Bramkjar", "Bram Thornevund").Should().BeEmpty(
            "the two names never share a turn, so nothing declared them the same person");
    }

    /// <summary>
    /// Sharing a SESSION is not a declaration; sharing a TURN is.
    /// </summary>
    /// <remarks>
    /// The bar is deliberately at the turn. A declaration is a sentence; two names elsewhere in a
    /// long conversation are co-presence, which is the weakest possible evidence and the one that
    /// would re-admit exactly what the previous test excludes.
    /// </remarks>
    [Fact]
    public void CoPresenceInASessionIsNotEnough()
    {
        var session = Session(
            "The new flat needs a doorbell.",
            "Separately: the place on Ferrow Row has a broken gate.");

        Aliases(session, "the new flat", "the place on Ferrow Row").Should().BeEmpty();
    }

    /// <summary>
    /// Sharing a TURN is not a declaration either — the bar is the sentence.
    /// </summary>
    /// <remarks>
    /// The turn was the first bar and the re-probe measured it too weak: "Thorne Bramkjar = Bram
    /// Thornevund" survived it, because these turns name several people apiece while the two names
    /// never share a sentence anywhere in the corpus. This is the same mistake as co-presence in a
    /// session, one notch finer, and it is the reason the bar moved twice.
    /// </remarks>
    [Fact]
    public void CoPresenceInATurnIsNotEnoughEither()
    {
        var session = Session(
            "Thorne Bramkjar said as much. Bram Thornevund mentioned it later, separately.");

        Aliases(session, "Thorne Bramkjar", "Bram Thornevund").Should().BeEmpty(
            "two names in one chatty turn are co-presence, not a statement that they are one person");
    }

    /// <summary>An entity aliased to its own name is noise, not an identity.</summary>
    [Fact]
    public void AnAliasEqualToTheNameIsDropped()
    {
        var session = Session("Head office is where it happened.");

        Aliases(session, "Head office", "head office").Should().BeEmpty();
    }

    /// <summary>Mixed emissions keep the supported one and drop the rest.</summary>
    [Fact]
    public void TheSupportedAliasSurvivesAlongsideAnUnsupportedOne()
    {
        var session = Session(
            "Head office is the Calderwick office.",
            "Thorne Bramkjar was there.");

        Aliases(session, "Head office", "Calderwick office", "Thorne Bramkjar")
            .Should().BeEquivalentTo(["Calderwick office"]);
    }
}
