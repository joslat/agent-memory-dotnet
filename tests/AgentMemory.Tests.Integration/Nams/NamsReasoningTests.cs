using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using AgentMemory.Nams.Client;

namespace AgentMemory.Tests.Integration.Nams;

/// <summary>
/// Live tests for the Phase 10h TCK Platinum reasoning/provenance additions: <see cref="INamsClient.RecordReasoningStepAsync"/>,
/// <see cref="INamsClient.ListReasoningStepsAsync"/>, <see cref="INamsClient.RecordToolCallAsync"/>,
/// <see cref="INamsClient.GetReasoningTraceAsync"/>, and <see cref="INamsClient.GetEntityProvenanceAsync"/>. See
/// <c>docs/reviews/NAMS_Phase10h_ReasoningProvenance_PlanningAndImplementationPlan.md</c> for the design.
/// Unlike Phase 10f's observations, recording and immediately reading back a reasoning step/tool call is a
/// direct, synchronous write/read with no async worker delay (confirmed live) -- so those get genuine
/// positive-assertion tests. Provenance links to async entity extraction (the same family as observations), so
/// it only gets a shape/wiring test, per that phase's hard-won lesson against forcing unreliable timing.
/// </summary>
[Collection("NAMS Live")]
[Trait("Category", "Integration")]
public sealed class NamsReasoningTests
{
    private readonly NamsLiveFixture _fixture;

    public NamsReasoningTests(NamsLiveFixture fixture) => _fixture = fixture;

    [LiveNamsFact]
    public async Task RecordReasoningStepAsync_ThenListReasoningStepsAsync_ReturnsTheRecordedStep()
    {
        var namsClient = _fixture.Services!.GetRequiredService<INamsClient>();
        var conversation = await namsClient.CreateConversationAsync(NamsLiveTestHelpers.UniqueUserId(), null, CancellationToken.None);
        var marker = Guid.NewGuid().ToString("N");

        try
        {
            var recorded = await namsClient.RecordReasoningStepAsync(
                conversation.Id, $"reasoning-{marker}", $"action-{marker}", $"result-{marker}", CancellationToken.None);
            recorded.ConversationId.Should().Be(conversation.Id);

            var steps = await namsClient.ListReasoningStepsAsync(conversation.Id, CancellationToken.None);

            steps.Should().ContainSingle(s => s.Id == recorded.Id).Which.Reasoning.Should().Be($"reasoning-{marker}");
        }
        finally
        {
            await namsClient.DeleteConversationAsync(conversation.Id, CancellationToken.None);
        }
    }

    [LiveNamsFact]
    public async Task RecordToolCallAsync_LinkedToAStep_AppearsInTheTrace()
    {
        var namsClient = _fixture.Services!.GetRequiredService<INamsClient>();
        var conversation = await namsClient.CreateConversationAsync(NamsLiveTestHelpers.UniqueUserId(), null, CancellationToken.None);
        var marker = Guid.NewGuid().ToString("N");

        try
        {
            var step = await namsClient.RecordReasoningStepAsync(
                conversation.Id, "deciding to search", $"search-{marker}", null, CancellationToken.None);
            var toolCall = await namsClient.RecordToolCallAsync(
                step.Id, $"tool-{marker}", "{\"query\":\"test\"}", "{\"results\":[]}", "success", 42, CancellationToken.None);

            var trace = await namsClient.GetReasoningTraceAsync(conversation.Id, CancellationToken.None);

            trace.ConversationId.Should().Be(conversation.Id);
            trace.Steps.Should().ContainSingle(s => s.Id == step.Id);
            var tracedToolCall = trace.ToolCalls.Should().ContainSingle(t => t.Id == toolCall.Id).Which;
            tracedToolCall.StepId.Should().Be(step.Id,
                "the trace must correctly link the tool call back to the step it was recorded against");
            tracedToolCall.ToolName.Should().Be($"tool-{marker}");
        }
        finally
        {
            await namsClient.DeleteConversationAsync(conversation.Id, CancellationToken.None);
        }
    }

    [LiveNamsFact]
    public async Task GetEntityProvenanceAsync_OnExistingEntity_ReturnsWellTypedResult()
    {
        var namsClient = _fixture.Services!.GetRequiredService<INamsClient>();
        var entityId = await NamsLiveTestHelpers.GetAnyExistingEntityIdAsync(namsClient, limit: 1, CancellationToken.None);

        var provenance = await namsClient.GetEntityProvenanceAsync(entityId, CancellationToken.None);

        provenance.EntityId.Should().Be(entityId,
            "the response must echo back the exact entity id queried, not some other one");
        provenance.Provenance.Should().NotBeNull();
        // Deliberately not asserting non-empty Provenance -- linking reasoning to entity extraction is async
        // worker machinery (the same family as Phase 10f's observations), and this phase's own design doc
        // explains why forcing that timing is out of scope here.
    }
}
