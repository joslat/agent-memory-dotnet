using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Core.Extraction;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction;

public sealed class PatternBasedPreferenceDetectorTests
{
    private static readonly PatternBasedPreferenceDetector Sut = new();

    private static Message UserMessage(string content) => new()
    {
        MessageId = Guid.NewGuid().ToString(),
        ConversationId = "c-1",
        SessionId = "s-1",
        Role = "user",
        Content = content,
        TimestampUtc = DateTimeOffset.UtcNow
    };

    // ── Empty / null guards ───────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_EmptyMessages_ReturnsEmpty()
    {
        var result = await Sut.ExtractAsync(Array.Empty<Message>());

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_BlankContent_ReturnsEmpty()
    {
        var result = await Sut.ExtractAsync([UserMessage("   ")]);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_NonPreferenceText_ReturnsEmpty()
    {
        var result = await Sut.ExtractAsync([UserMessage("The capital of France is Paris.")]);

        result.Should().BeEmpty();
    }

    // ── Communication style ───────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_CommunicationStylePreference_Detected()
    {
        var result = await Sut.ExtractAsync([UserMessage("I prefer concise answers.")]);

        result.Should().ContainSingle(p => p.Category == "communication_style");
    }

    [Fact]
    public async Task ExtractAsync_PleaseAlwaysPattern_DetectedAsCommunicationStyle()
    {
        var result = await Sut.ExtractAsync([UserMessage("Please always provide code examples with explanations.")]);

        result.Should().ContainSingle(p => p.Category == "communication_style");
    }

    [Fact]
    public async Task ExtractAsync_StrongCommunicationPattern_HasHighConfidence()
    {
        var result = await Sut.ExtractAsync([UserMessage("I prefer concise answers.")]);

        result.Should().ContainSingle().Which.Confidence.Should().Be(0.95);
    }

    // ── Format preferences ────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_BulletPointsPreference_Detected()
    {
        var result = await Sut.ExtractAsync([UserMessage("Use bullet points in your responses.")]);

        result.Should().ContainSingle(p => p.Category == "format");
    }

    [Fact]
    public async Task ExtractAsync_JsonFormatPreference_Detected()
    {
        var result = await Sut.ExtractAsync([UserMessage("I want JSON format for all API responses.")]);

        result.Should().ContainSingle(p => p.Category == "format");
    }

    [Fact]
    public async Task ExtractAsync_AlwaysIncludeCodeExamples_DetectedAsFormat()
    {
        var result = await Sut.ExtractAsync([UserMessage("Always include code examples when explaining concepts.")]);

        result.Should().ContainSingle(p => p.Category == "format");
    }

    [Fact]
    public async Task ExtractAsync_FormatPreference_HasRegexConfidence()
    {
        var result = await Sut.ExtractAsync([UserMessage("Use bullet points for lists.")]);

        result.Should().ContainSingle().Which.Confidence.Should().Be(0.85);
    }

    // ── Tool preferences ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_IPreferUsing_DetectedAsToolPreference()
    {
        var result = await Sut.ExtractAsync([UserMessage("I prefer using VS Code for development.")]);

        result.Should().Contain(p => p.Category == "tool_preference");
    }

    [Fact]
    public async Task ExtractAsync_DontUsePattern_DetectedAsToolPreference()
    {
        var result = await Sut.ExtractAsync([UserMessage("Don't use tabs in the generated code.")]);

        result.Should().ContainSingle(p => p.Category == "tool_preference");
    }

    [Fact]
    public async Task ExtractAsync_StrongToolPattern_HasHighConfidence()
    {
        var result = await Sut.ExtractAsync([UserMessage("Don't use legacy APIs.")]);

        result.Should().ContainSingle(p => p.Category == "tool_preference")
            .Which.Confidence.Should().Be(0.95);
    }

    // ── Language / tone ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_KeepItFormal_DetectedAsLanguageTone()
    {
        var result = await Sut.ExtractAsync([UserMessage("Keep it formal in all responses.")]);

        result.Should().ContainSingle(p => p.Category == "language_tone");
    }

    [Fact]
    public async Task ExtractAsync_BeCasual_DetectedAsLanguageTone()
    {
        var result = await Sut.ExtractAsync([UserMessage("Be casual and friendly in your responses.")]);

        result.Should().Contain(p => p.Category == "language_tone");
    }

    [Fact]
    public async Task ExtractAsync_TechnicalLanguage_DetectedAsLanguageTone()
    {
        var result = await Sut.ExtractAsync([UserMessage("Use technical language, I'm a developer.")]);

        result.Should().ContainSingle(p => p.Category == "language_tone");
    }

    [Fact]
    public async Task ExtractAsync_FormalTone_HasHighConfidence()
    {
        var result = await Sut.ExtractAsync([UserMessage("Keep it formal in meetings.")]);

        result.Should().ContainSingle(p => p.Category == "language_tone")
            .Which.Confidence.Should().Be(0.95);
    }

    // ── Time / scheduling ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_TimezoneStatement_DetectedAsTimeScheduling()
    {
        var result = await Sut.ExtractAsync([UserMessage("My timezone is Europe/Madrid.")]);

        result.Should().ContainSingle(p => p.Category == "time_scheduling");
    }

    [Fact]
    public async Task ExtractAsync_IWorkIn_DetectedAsTimeScheduling()
    {
        var result = await Sut.ExtractAsync([UserMessage("I work in the mornings, usually from 8am to 1pm.")]);

        result.Should().Contain(p => p.Category == "time_scheduling");
    }

    [Fact]
    public async Task ExtractAsync_ImAvailable_DetectedAsTimeScheduling()
    {
        var result = await Sut.ExtractAsync([UserMessage("I'm available on weekdays after 3pm.")]);

        result.Should().ContainSingle(p => p.Category == "time_scheduling");
    }

    [Fact]
    public async Task ExtractAsync_TimezonePattern_HasHighConfidence()
    {
        var result = await Sut.ExtractAsync([UserMessage("My timezone is UTC+2.")]);

        result.Should().ContainSingle(p => p.Category == "time_scheduling")
            .Which.Confidence.Should().Be(0.95);
    }

    // ── Privacy / sharing ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_DontShare_DetectedAsPrivacy()
    {
        var result = await Sut.ExtractAsync([UserMessage("Don't share my personal data with third parties.")]);

        result.Should().ContainSingle(p => p.Category == "privacy");
    }

    [Fact]
    public async Task ExtractAsync_KeepPrivate_DetectedAsPrivacy()
    {
        var result = await Sut.ExtractAsync([UserMessage("Keep this conversation private.")]);

        result.Should().ContainSingle(p => p.Category == "privacy");
    }

    [Fact]
    public async Task ExtractAsync_YouCanShare_DetectedAsPrivacy()
    {
        var result = await Sut.ExtractAsync([UserMessage("You can share my public profile with collaborators.")]);

        result.Should().ContainSingle(p => p.Category == "privacy");
    }

    [Fact]
    public async Task ExtractAsync_PrivacyPattern_HasHighConfidence()
    {
        var result = await Sut.ExtractAsync([UserMessage("Don't share my email address.")]);

        result.Should().ContainSingle(p => p.Category == "privacy")
            .Which.Confidence.Should().Be(0.95);
    }

    // ── Content preferences ───────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_InterestedIn_DetectedAsContent()
    {
        var result = await Sut.ExtractAsync([UserMessage("I'm interested in machine learning and AI topics.")]);

        result.Should().ContainSingle(p => p.Category == "content");
    }

    [Fact]
    public async Task ExtractAsync_FocusOn_DetectedAsContent()
    {
        var result = await Sut.ExtractAsync([UserMessage("Focus on practical examples rather than theory.")]);

        result.Should().ContainSingle(p => p.Category == "content");
    }

    [Fact]
    public async Task ExtractAsync_Skip_DetectedAsContent()
    {
        var result = await Sut.ExtractAsync([UserMessage("Skip the lengthy introductions in your answers.")]);

        result.Should().ContainSingle(p => p.Category == "content");
    }

    // ── Multiple preferences in one message ───────────────────────────────────

    [Fact]
    public async Task ExtractAsync_MultiplePreferencesInOneMessage_AllDetected()
    {
        var text = "I prefer concise answers. Use bullet points. My timezone is UTC+1.";
        var result = await Sut.ExtractAsync([UserMessage(text)]);

        result.Should().HaveCountGreaterThanOrEqualTo(3);
        result.Select(p => p.Category).Should().Contain("communication_style");
        result.Select(p => p.Category).Should().Contain("format");
        result.Select(p => p.Category).Should().Contain("time_scheduling");
    }

    [Fact]
    public async Task ExtractAsync_MultipleMessages_ExtractsFromAll()
    {
        var messages = new[]
        {
            UserMessage("I prefer concise answers."),
            UserMessage("My timezone is UTC+2."),
        };

        var result = await Sut.ExtractAsync(messages);

        result.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    // ── Confidence scores ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_AllConfidenceScoresAreValid()
    {
        var text = "I prefer concise answers. Use bullet points. Don't share my data. My timezone is UTC.";
        var result = await Sut.ExtractAsync([UserMessage(text)]);

        result.Should().AllSatisfy(p =>
            p.Confidence.Should().BeInRange(0.0, 1.0));
    }

    [Fact]
    public async Task ExtractAsync_RegexMatch_HasConfidence085()
    {
        var result = await Sut.ExtractAsync([UserMessage("Use bullet points for your list.")]);

        result.Should().ContainSingle(p => p.Category == "format")
            .Which.Confidence.Should().Be(0.85);
    }

    [Fact]
    public async Task ExtractAsync_StrongPattern_HasConfidence095()
    {
        var result = await Sut.ExtractAsync([UserMessage("Don't share my phone number.")]);

        result.Should().ContainSingle(p => p.Category == "privacy")
            .Which.Confidence.Should().Be(0.95);
    }

    // ── PreferenceText / Context populated ───────────────────────────────────

    [Fact]
    public async Task ExtractAsync_PreferenceText_IsNotEmpty()
    {
        var result = await Sut.ExtractAsync([UserMessage("I prefer concise answers.")]);

        result.Should().ContainSingle().Which.PreferenceText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExtractAsync_Context_ContainsRoleInfo()
    {
        var result = await Sut.ExtractAsync([UserMessage("I prefer concise answers.")]);

        result.Should().ContainSingle().Which.Context.Should().Contain("user");
    }
}

