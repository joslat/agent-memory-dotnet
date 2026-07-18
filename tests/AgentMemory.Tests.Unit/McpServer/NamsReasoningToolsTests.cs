using System.Text.Json;
using FluentAssertions;
using AgentMemory.McpServer.Nams.Tools;
using AgentMemory.Nams.Domain;
using AgentMemory.Tests.Unit.Nams;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class NamsReasoningToolsTests
{
    private sealed class FakeNamsClient : ThrowingNamsClientStub
    {
        public Func<string, CancellationToken, Task<IReadOnlyList<NamsReasoningStep>>>? OnListReasoningSteps { get; set; }
        public Func<string, CancellationToken, Task<NamsReasoningTrace>>? OnGetReasoningTrace { get; set; }
        public Func<string, CancellationToken, Task<NamsEntityProvenance>>? OnGetEntityProvenance { get; set; }
        public Func<string, string, string, string?, CancellationToken, Task<NamsReasoningStep>>? OnRecordReasoningStep { get; set; }
        public Func<string?, string, string, string?, string?, int?, CancellationToken, Task<NamsToolCall>>? OnRecordToolCall { get; set; }

        public override Task<IReadOnlyList<NamsReasoningStep>> ListReasoningStepsAsync(string conversationId, CancellationToken cancellationToken) =>
            (OnListReasoningSteps ?? ((_, _) => Task.FromResult<IReadOnlyList<NamsReasoningStep>>([])))(conversationId, cancellationToken);

        public override Task<NamsReasoningTrace> GetReasoningTraceAsync(string conversationId, CancellationToken cancellationToken) =>
            (OnGetReasoningTrace ?? ((id, _) => Task.FromResult(new NamsReasoningTrace(id, [], []))))(conversationId, cancellationToken);

        public override Task<NamsEntityProvenance> GetEntityProvenanceAsync(string entityId, CancellationToken cancellationToken) =>
            (OnGetEntityProvenance ?? ((id, _) => Task.FromResult(new NamsEntityProvenance(id, []))))(entityId, cancellationToken);

        public override Task<NamsReasoningStep> RecordReasoningStepAsync(
            string conversationId, string reasoning, string actionTaken, string? result, CancellationToken cancellationToken) =>
            (OnRecordReasoningStep ?? ((cid, r, a, res, _) => Task.FromResult(new NamsReasoningStep("s1", cid, r, a, res, "2026-01-01"))))
            (conversationId, reasoning, actionTaken, result, cancellationToken);

        public override Task<NamsToolCall> RecordToolCallAsync(
            string? stepId, string toolName, string input, string? output, string? status, int? durationMs,
            CancellationToken cancellationToken) =>
            (OnRecordToolCall ?? ((sid, name, i, o, st, dur, _) => Task.FromResult(new NamsToolCall("tc1", sid, name, st ?? "success", i, o, dur, "2026-01-01"))))
            (stepId, toolName, input, output, status, durationMs, cancellationToken);
    }

    // ---- nams_list_reasoning_steps ----

    [Fact]
    public async Task NamsListReasoningSteps_MissingConversationId_ReturnsError()
    {
        var json = await NamsReasoningReadTools.NamsListReasoningSteps(new FakeNamsClient(), "", CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("namsConversationId");
    }

    [Fact]
    public async Task NamsListReasoningSteps_ReturnsSteps()
    {
        var client = new FakeNamsClient
        {
            OnListReasoningSteps = (cid, _) => Task.FromResult<IReadOnlyList<NamsReasoningStep>>(
                [new NamsReasoningStep("s1", cid, "because X", "did Y", "worked", "2026-01-01")])
        };

        var json = await NamsReasoningReadTools.NamsListReasoningSteps(client, "conv-1", CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("steps")[0].GetProperty("reasoning").GetString().Should().Be("because X");
    }

    // ---- nams_reasoning_trace ----

    [Fact]
    public async Task NamsReasoningTrace_MissingConversationId_ReturnsError()
    {
        var json = await NamsReasoningReadTools.NamsReasoningTrace(new FakeNamsClient(), null!, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("namsConversationId");
    }

    [Fact]
    public async Task NamsReasoningTrace_ReturnsStepsAndToolCalls()
    {
        var client = new FakeNamsClient
        {
            OnGetReasoningTrace = (cid, _) => Task.FromResult(new NamsReasoningTrace(
                cid,
                [new NamsReasoningStep("s1", cid, "r", "a", null, "2026-01-01")],
                [new NamsToolCall("tc1", "s1", "search", "success", "{}", "{}", 12, "2026-01-01")]))
        };

        var json = await NamsReasoningReadTools.NamsReasoningTrace(client, "conv-1", CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("conversationId").GetString().Should().Be("conv-1");
        doc.RootElement.GetProperty("steps")[0].GetProperty("id").GetString().Should().Be("s1");
        doc.RootElement.GetProperty("toolCalls")[0].GetProperty("toolName").GetString().Should().Be("search");
    }

    // ---- nams_entity_provenance ----

    [Fact]
    public async Task NamsEntityProvenance_MissingEntityId_ReturnsError()
    {
        var json = await NamsReasoningReadTools.NamsEntityProvenance(new FakeNamsClient(), " ", CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("entityId");
    }

    [Fact]
    public async Task NamsEntityProvenance_EmptyProvenance_ReturnsEmptyArray()
    {
        var json = await NamsReasoningReadTools.NamsEntityProvenance(new FakeNamsClient(), "e1", CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("entityId").GetString().Should().Be("e1");
        doc.RootElement.GetProperty("provenance").GetArrayLength().Should().Be(0);
    }

    // ---- nams_record_reasoning_step ----

    [Theory]
    [InlineData("", "reasoning", "action")]
    [InlineData("conv-1", "", "action")]
    [InlineData("conv-1", "reasoning", "")]
    public async Task NamsRecordReasoningStep_MissingRequiredField_ReturnsError(string conversationId, string reasoning, string actionTaken)
    {
        var json = await NamsReasoningWriteTools.NamsRecordReasoningStep(
            new FakeNamsClient(), conversationId, reasoning, actionTaken, null, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public async Task NamsRecordReasoningStep_ValidRequest_ReturnsRecordedStep()
    {
        var json = await NamsReasoningWriteTools.NamsRecordReasoningStep(
            new FakeNamsClient(), "conv-1", "because X", "did Y", "worked", CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("conversationId").GetString().Should().Be("conv-1");
        doc.RootElement.GetProperty("reasoning").GetString().Should().Be("because X");
    }

    // ---- nams_record_tool_call ----

    [Fact]
    public async Task NamsRecordToolCall_MissingToolName_ReturnsError()
    {
        var json = await NamsReasoningWriteTools.NamsRecordToolCall(
            new FakeNamsClient(), null, "", "{}", null, null, null, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("toolName");
    }

    [Fact]
    public async Task NamsRecordToolCall_MissingInput_ReturnsError()
    {
        var json = await NamsReasoningWriteTools.NamsRecordToolCall(
            new FakeNamsClient(), null, "search", " ", null, null, null, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("input");
    }

    [Fact]
    public async Task NamsRecordToolCall_ValidRequest_ReturnsRecordedCall()
    {
        var json = await NamsReasoningWriteTools.NamsRecordToolCall(
            new FakeNamsClient(), "s1", "search", "{\"q\":\"x\"}", "{\"r\":1}", "success", 42, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("toolName").GetString().Should().Be("search");
        doc.RootElement.GetProperty("stepId").GetString().Should().Be("s1");
        doc.RootElement.GetProperty("durationMs").GetInt32().Should().Be(42);
    }
}
