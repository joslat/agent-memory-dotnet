using FluentAssertions;
using Microsoft.Extensions.AI;
using Neo4j.AgentMemory.McpServer.Prompts;

namespace Neo4j.AgentMemory.Tests.Unit.McpServer;

public sealed class MemoryPromptsTests
{
    // ── MemoryConversationPrompt ──────────────────────────────────────────────

    [Fact]
    public void MemoryConversationPrompt_ReturnsMessages()
    {
        var result = MemoryConversationPrompt.MemoryConversation().ToList();

        result.Should().NotBeEmpty();
    }

    [Fact]
    public void MemoryConversationPrompt_HasUserRole()
    {
        var result = MemoryConversationPrompt.MemoryConversation().ToList();

        result.Should().AllSatisfy(m => m.Role.Should().Be(ChatRole.User));
    }

    [Fact]
    public void MemoryConversationPrompt_ContainsMemoryGetContextInstruction()
    {
        var text = GetAllText(MemoryConversationPrompt.MemoryConversation());

        text.Should().Contain("memory_get_context");
    }

    [Fact]
    public void MemoryConversationPrompt_ContainsMemoryStoreMessageInstruction()
    {
        var text = GetAllText(MemoryConversationPrompt.MemoryConversation());

        text.Should().Contain("memory_store_message");
    }

    [Fact]
    public void MemoryConversationPrompt_ContainsMemoryAddPreferenceInstruction()
    {
        var text = GetAllText(MemoryConversationPrompt.MemoryConversation());

        text.Should().Contain("memory_add_preference");
    }

    [Fact]
    public void MemoryConversationPrompt_MentionsAvailableToolsList()
    {
        var text = GetAllText(MemoryConversationPrompt.MemoryConversation());

        text.Should().Contain("memory_add_entity");
        text.Should().Contain("memory_search");
    }

    [Fact]
    public void MemoryConversationPrompt_WithSessionId_IncludesSessionIdInContent()
    {
        var text = GetAllText(MemoryConversationPrompt.MemoryConversation("my-session-42"));

        text.Should().Contain("my-session-42");
    }

    [Fact]
    public void MemoryConversationPrompt_WithoutSessionId_StillProducesContent()
    {
        var result = MemoryConversationPrompt.MemoryConversation("").ToList();

        result.Should().NotBeEmpty();
        GetAllText(result).Should().NotBeNullOrWhiteSpace();
    }

    // ── MemoryReasoningPrompt ─────────────────────────────────────────────────

    [Fact]
    public void MemoryReasoningPrompt_ReturnsMessages()
    {
        var result = MemoryReasoningPrompt.MemoryReasoning("Calculate pi").ToList();

        result.Should().NotBeEmpty();
    }

    [Fact]
    public void MemoryReasoningPrompt_HasUserRole()
    {
        var result = MemoryReasoningPrompt.MemoryReasoning("some task").ToList();

        result.Should().AllSatisfy(m => m.Role.Should().Be(ChatRole.User));
    }

    [Fact]
    public void MemoryReasoningPrompt_IncludesTaskInContent()
    {
        var text = GetAllText(MemoryReasoningPrompt.MemoryReasoning("find the best route"));

        text.Should().Contain("find the best route");
    }

    [Fact]
    public void MemoryReasoningPrompt_ContainsStartTraceInstruction()
    {
        var text = GetAllText(MemoryReasoningPrompt.MemoryReasoning("task"));

        text.Should().Contain("memory_start_trace");
    }

    [Fact]
    public void MemoryReasoningPrompt_ContainsRecordStepInstruction()
    {
        var text = GetAllText(MemoryReasoningPrompt.MemoryReasoning("task"));

        text.Should().Contain("memory_record_step");
    }

    [Fact]
    public void MemoryReasoningPrompt_ContainsCompleteTraceInstruction()
    {
        var text = GetAllText(MemoryReasoningPrompt.MemoryReasoning("task"));

        text.Should().Contain("memory_complete_trace");
    }

    [Fact]
    public void MemoryReasoningPrompt_MentionsThoughtActionObservation()
    {
        var text = GetAllText(MemoryReasoningPrompt.MemoryReasoning("task"));

        text.Should().Contain("thought");
        text.Should().Contain("action");
        text.Should().Contain("observation");
    }

    // ── MemoryReviewPrompt ────────────────────────────────────────────────────

    [Fact]
    public void MemoryReviewPrompt_ReturnsMessages()
    {
        var result = MemoryReviewPrompt.MemoryReview().ToList();

        result.Should().NotBeEmpty();
    }

    [Fact]
    public void MemoryReviewPrompt_HasUserRole()
    {
        var result = MemoryReviewPrompt.MemoryReview().ToList();

        result.Should().AllSatisfy(m => m.Role.Should().Be(ChatRole.User));
    }

    [Fact]
    public void MemoryReviewPrompt_ContainsMemorySearchInstruction()
    {
        var text = GetAllText(MemoryReviewPrompt.MemoryReview());

        text.Should().Contain("memory_search");
    }

    [Fact]
    public void MemoryReviewPrompt_ContainsMemoryListSessionsInstruction()
    {
        var text = GetAllText(MemoryReviewPrompt.MemoryReview());

        text.Should().Contain("memory_list_sessions");
    }

    [Fact]
    public void MemoryReviewPrompt_MentionsEntitiesPreferencesFacts()
    {
        var text = GetAllText(MemoryReviewPrompt.MemoryReview());

        text.Should().Contain("entities");
        text.Should().Contain("preferences");
        text.Should().Contain("facts");
    }

    [Fact]
    public void MemoryReviewPrompt_MentionsContradictions()
    {
        var text = GetAllText(MemoryReviewPrompt.MemoryReview());

        text.Should().ContainAny("contradiction", "contradictions");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetAllText(IEnumerable<ChatMessage> messages) =>
        string.Concat(messages.Select(m => string.Concat(m.Contents.OfType<TextContent>().Select(c => c.Text))));
}
