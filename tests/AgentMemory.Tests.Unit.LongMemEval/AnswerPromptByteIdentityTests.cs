using System.Security.Cryptography;
using System.Text;
using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 30.2. The off-state fingerprint for the surface the measurement plan actually names.
/// </summary>
/// <remarks>
/// <para>
/// The projection design seals the Core formatter and the Agent Framework mapper. This is the third
/// surface, and it is the one whose bytes every published band was computed over: void witness #1 of
/// the measurement plan says an off-arm prompt that does not match the sealed baseline voids the whole
/// comparison, because the instrument changed rather than the treatment.
/// </para>
/// <para>
/// Captured from the build immediately before projection reached this file. <b>Never regenerate to
/// make a change pass.</b> A failure here means the archive's prompts moved.
/// </para>
/// </remarks>
public sealed class AnswerPromptByteIdentityTests
{
    private const string AnswerPromptSha256 = "d3f3378e1d30c4dbdd85c1903ecb88470433a7a336632c8af83b0f4ee895e427";

    private static readonly DateTimeOffset Stamp = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static MemoryContext BuildContext() => new()
    {
        SessionId = "s1",
        AssembledAtUtc = Stamp,
        RelevantMessages = new MemoryContextSection<Message>
        {
            Items =
            [
                new Message
                {
                    MessageId = "m1", SessionId = "s1", ConversationId = "c1",
                    Role = "user", Content = "I joined Acme last spring.", TimestampUtc = Stamp,
                },
            ],
        },
        RelevantEntities = new MemoryContextSection<Entity>
        {
            Items =
            [
                new Entity
                {
                    EntityId = "e1", Name = "Acme", Type = "ORGANIZATION",
                    Description = "a company", Confidence = 0.9, CreatedAtUtc = Stamp,
                    SourceMessageIds = ["m1"],
                },
                new Entity
                {
                    EntityId = "e2", Name = "Bob", Type = "PERSON",
                    Confidence = 0.8, CreatedAtUtc = Stamp,
                },
            ],
        },
        RelevantFacts = new MemoryContextSection<Fact>
        {
            Items =
            [
                new Fact
                {
                    FactId = "f1", Subject = "Bob", Predicate = "works_at", Object = "Acme",
                    Confidence = 0.95, CreatedAtUtc = Stamp, SourceMessageIds = ["m1"],
                },
                // Exercises the valid-window branch, which only some facts take.
                new Fact
                {
                    FactId = "f2", Subject = "Bob", Predicate = "lives_in", Object = "Zurich",
                    Confidence = 0.7, CreatedAtUtc = Stamp,
                    ValidFrom = Stamp, ValidUntil = Stamp.AddYears(1),
                },
            ],
        },
        RelevantPreferences = new MemoryContextSection<Preference>
        {
            Items =
            [
                new Preference
                {
                    PreferenceId = "p1", Category = "food", PreferenceText = "prefers vegetarian",
                    Context = "at work", Confidence = 0.9, CreatedAtUtc = Stamp,
                },
            ],
        },
        GraphRagContext = "graph knowledge block",
    };

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Canonical(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void TheAnswerPromptIsUnchangedWithProjectionOff()
    {
        var prompt = AgentMemoryLongMemEvalAdapter.BuildAnswerPrompt(
            BuildContext(), "Who does Bob work for?", "2026-03-04");

        Sha256(Canonical(prompt)).Should().Be(AnswerPromptSha256,
            "these are the prompt bytes every published band was computed over; a change here voids "
            + "the comparison rather than needing a new snapshot");
    }

    [Fact]
    public void TheFixtureExercisesEverySectionThePromptCanRender()
    {
        var prompt = AgentMemoryLongMemEvalAdapter.BuildAnswerPrompt(
            BuildContext(), "Who does Bob work for?", "2026-03-04");

        prompt.Should().Contain("[entity").And.Contain("[fact").And.Contain("[preference")
            .And.Contain("[graphrag").And.Contain("[valid ");
    }
}
