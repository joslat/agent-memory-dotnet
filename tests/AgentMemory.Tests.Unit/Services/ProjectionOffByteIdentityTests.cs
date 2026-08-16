using System.Security.Cryptography;
using System.Text;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.AgentFramework.Mapping;
using AgentMemory.Core.Services;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.2 step 2. The byte-identical-when-off proof for the projection layer, and the **only** test in
/// this feature written before any of its code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a fingerprint and not a set of behavioural assertions.</b> Projection touches the three
/// surfaces that render recalled memory into a prompt. Every measurement this project has sealed —
/// the published 76–90% structured band at 403 tok/q, every per-type slice, every abstention count —
/// was taken over specific prompt BYTES. If those bytes move while the flags are off, no on-arm/off-arm
/// comparison means anything and the entire archive stops being a baseline. A behavioural assertion
/// checks the thing someone thought to check; a hash over the whole rendered string checks everything,
/// including the whitespace and ordering nobody would think to assert.
/// </para>
/// <para>
/// <b>This file is written FIRST and must never be edited to make a later step pass.</b> The hashes
/// below were captured from the build immediately preceding any projection code. A failure here means
/// the off-state changed — which is void witness #1 of the measurement plan, a full stop, not a
/// snapshot to regenerate. If a deliberate rendering change is ever made on purpose, it invalidates
/// every sealed comparison and that is the conversation to have, loudly.
/// </para>
/// <para>
/// The fixture is deliberately maximal: every section the formatter can emit (recent and relevant
/// messages, entities, facts, preferences, traces, GraphRAG), both blend orders, a three-state trace
/// success, an entity with and without a description, and multi-line content. A fingerprint over a
/// thin fixture would pass while a section nobody exercised drifted.
/// </para>
/// </remarks>
public sealed class ProjectionOffByteIdentityTests
{
    // ── the sealed hashes ────────────────────────────────────────────────
    // Captured 2026-08-15 from the build immediately before projection existed.
    private const string FormatterBlendedSha256 =
        "a650ab4fb7400a399c9ef94b71b95d83df8acc54b9e91e3edd0df7884ab116d4";
    private const string FormatterGraphFirstSha256 =
        "53949444dc08c8948280231b0c43de0d7e31cfbfe8d3baf3fdd66bcb484fb725";
    private const string MafContextMessagesSha256 =
        "e7f50e0b94494502d24e8bd6393fc7200481db9dbde2afc02a551a96797ad40e";

    private static readonly DateTimeOffset Stamp =
        new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static Message Msg(string id, string role, string content) => new()
    {
        MessageId = id,
        SessionId = "s1",
        ConversationId = "c1",
        Role = role,
        Content = content,
        TimestampUtc = Stamp,
    };

    /// <summary>The maximal fixture — every section, deterministic values, no clock reads.</summary>
    private static MemoryContext BuildContext(RetrievalBlendMode mode) => new()
    {
        SessionId = "s1",
        AssembledAtUtc = Stamp,
        BlendMode = mode,
        RecentMessages = new MemoryContextSection<Message>
        {
            Items = [Msg("m1", "user", "hello memory"), Msg("m2", "assistant", "line one\nline two")],
        },
        RelevantMessages = new MemoryContextSection<Message>
        {
            Items = [Msg("m3", "user", "an older but relevant thing")],
        },
        RelevantEntities = new MemoryContextSection<Entity>
        {
            Items =
            [
                new Entity
                {
                    EntityId = "e1", Name = "Acme", Type = "ORGANIZATION",
                    Description = "a company", Confidence = 0.9, CreatedAtUtc = Stamp,
                },
                // No description: exercises the other branch of the entity describe lambda.
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
                    Confidence = 0.95, CreatedAtUtc = Stamp,
                    SourceMessageIds = ["m1"],
                },
                new Fact
                {
                    FactId = "f2", Subject = "Bob", Predicate = "lives_in", Object = "Zurich",
                    Confidence = 0.7, CreatedAtUtc = Stamp,
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
                    Confidence = 0.9, CreatedAtUtc = Stamp,
                },
            ],
        },
        SimilarTraces = new MemoryContextSection<ReasoningTrace>
        {
            Items =
            [
                // All three success states, because the formatter renders ✓ / ✗ / ? distinctly and
                // the null case is the one that has been got wrong before.
                new ReasoningTrace
                {
                    TraceId = "t1", SessionId = "s1", Task = "book a flight",
                    StartedAtUtc = Stamp, Success = true, Outcome = "booked LX318",
                },
                new ReasoningTrace
                {
                    TraceId = "t2", SessionId = "s1", Task = "cancel a flight",
                    StartedAtUtc = Stamp, Success = false, Outcome = "refund window closed",
                },
                new ReasoningTrace
                {
                    TraceId = "t3", SessionId = "s1", Task = "check in",
                    StartedAtUtc = Stamp, Success = null,
                },
            ],
        },
        GraphRagContext = "graph knowledge block",
    };

    private static RecallResult BuildResult(RetrievalBlendMode mode) => new()
    {
        Context = BuildContext(mode),
        TotalItemsRetrieved = 11,
    };

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Normalises line endings so the hash describes content, not the checkout's autocrlf.</summary>
    private static string Canonical(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void TheCoreFormatterOutputIsUnchangedWithProjectionOff()
    {
        var rendered = MemoryContextFormatter.FormatRecallResult(BuildResult(RetrievalBlendMode.Blended));

        rendered.Should().NotBeEmpty();
        Sha256(Canonical(rendered)).Should().Be(FormatterBlendedSha256,
            "the off-state prompt bytes are the baseline every sealed measurement was taken over; "
            + "a change here voids the archive, it does not need a new snapshot");
    }

    [Fact]
    public void TheCoreFormatterGraphFirstOutputIsUnchangedWithProjectionOff()
    {
        var rendered = MemoryContextFormatter.FormatRecallResult(
            BuildResult(RetrievalBlendMode.GraphRagThenMemory));

        rendered.Should().NotBeEmpty();
        Sha256(Canonical(rendered)).Should().Be(FormatterGraphFirstSha256);
    }

    [Fact]
    public void TheAgentFrameworkMapperOutputIsUnchangedWithProjectionOff()
    {
        // Fingerprinted as role + text pairs rather than as ChatMessage.ToString(), so the hash covers
        // the two things this surface decides -- what is said and with what authority -- and does not
        // move if the SDK changes its own formatting.
        var messages = MafTypeMapper.ToContextMessages(BuildContext(RetrievalBlendMode.Blended));

        messages.Should().NotBeEmpty();
        var canonical = string.Join("\n---\n",
            messages.Select(m => $"{m.Role}{Canonical(m.Text ?? string.Empty)}"));

        Sha256(canonical).Should().Be(MafContextMessagesSha256);
    }

    [Fact]
    public void TheFixtureExercisesEverySectionTheFormatterCanRender()
    {
        // A fingerprint over a fixture that misses a section silently stops protecting that section.
        var rendered = MemoryContextFormatter.FormatRecallResult(BuildResult(RetrievalBlendMode.Blended));

        rendered.Should().Contain("### Recent Messages")
            .And.Contain("### Relevant Past Messages")
            .And.Contain("### Known Entities")
            .And.Contain("### Known Facts")
            .And.Contain("### User Preferences")
            .And.Contain("### Similar Past Tasks")
            .And.Contain("### Graph Context");
    }
}
