using System.Reflection;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Does the conversation the model actually receives carry the turn's own date?
/// </summary>
/// <remarks>
/// <para>
/// This exists because the answer was ASSUMED. Extraction with
/// <c>TemporalValidityMode.Extract</c> returned validity windows dated from the extraction machine's
/// present — 41 windows, 0 before today — against a corpus set months earlier, and the instruction
/// was then anchored to "the timestamp of the turn that states it". The anchor changed nothing.
/// </para>
/// <para>
/// Two explanations remained and they have very different fixes: either the model ignores a
/// reference it can see, or the reference never reaches it. Reading the renderer suggested the
/// former. <b>Reading is not verifying</b>, and this project has today found nine separate cases
/// where a feature that read as wired was not.
/// </para>
/// </remarks>
public sealed class ExtractionPromptTimestampTests
{
    [Fact]
    public void TheBatchTextCarriesEachTurnsOwnTimestamp()
    {
        var when = new DateTimeOffset(2026, 5, 7, 9, 30, 0, TimeSpan.Zero);
        var request = new ExtractionRequest
        {
            SessionId = "s1",
            Messages =
            [
                new Message
                {
                    MessageId = "m1", SessionId = "s1", ConversationId = "c1", Role = "user",
                    Content = "I switch to the new gym from next Monday.", TimestampUtc = when,
                },
            ],
        };

        var method = typeof(LlmMultiSessionUnifiedMemoryExtractor).GetMethod(
            "BuildBatchText", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("the batch path builds the conversation the model sees");

        var text = (string)method!.Invoke(null, [new[] { request }, ExtractionProvenanceMode.Batch])!;

        text.Should().Contain("2026-05-07",
            "the model cannot anchor a relative date to a reference it was never shown — and if this "
            + "fails, the machine-anchored validity windows are a PLUMBING defect with a cheap fix, "
            + "not an LLM-behaviour problem");
        text.Should().Contain("next Monday", "the turn's own text must survive alongside its date");
    }
}
