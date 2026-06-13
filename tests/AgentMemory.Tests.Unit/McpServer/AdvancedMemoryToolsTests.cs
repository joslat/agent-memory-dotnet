using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpServer;
using AgentMemory.McpServer.Tools;
using NSubstitute;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class AdvancedMemoryToolsTests
{
    private readonly IReasoningMemoryService _reasoningMemory = Substitute.For<IReasoningMemoryService>();
    private readonly IGraphQueryService _graphQueryService = Substitute.For<IGraphQueryService>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IIdGenerator _idGenerator = Substitute.For<IIdGenerator>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private static readonly DateTimeOffset FixedTime = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public AdvancedMemoryToolsTests()
    {
        _idGenerator.GenerateId().Returns("gen-id-1");
        _clock.UtcNow.Returns(FixedTime);
    }

    // ── memory_record_tool_call ──

    [Fact]
    public async Task MemoryRecordToolCall_CallsRecordToolCallAsyncWithCorrectParameters()
    {
        var toolCall = new ToolCall
        {
            ToolCallId = "tc-1", StepId = "step-1", ToolName = "my_tool",
            ArgumentsJson = "{}", Status = ToolCallStatus.Success
        };
        _reasoningMemory.RecordToolCallAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<ToolCallStatus>(), Arg.Any<long?>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(toolCall);

        await AdvancedMemoryTools.MemoryRecordToolCall(
            _reasoningMemory, "step-1", "my_tool", "{\"key\":\"value\"}", null, "Success");

        await _reasoningMemory.Received(1).RecordToolCallAsync(
            "step-1", "my_tool", "{\"key\":\"value\"}", null, ToolCallStatus.Success,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryRecordToolCall_ReturnsJsonWithToolCallProperties()
    {
        var toolCall = new ToolCall
        {
            ToolCallId = "tc-1", StepId = "step-1", ToolName = "my_tool",
            ArgumentsJson = "{}", Status = ToolCallStatus.Success
        };
        _reasoningMemory.RecordToolCallAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<ToolCallStatus>(), Arg.Any<long?>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(toolCall);

        var result = await AdvancedMemoryTools.MemoryRecordToolCall(
            _reasoningMemory, "step-1", "my_tool", "{}");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("toolCallId").GetString().Should().Be("tc-1");
        doc.RootElement.GetProperty("toolName").GetString().Should().Be("my_tool");
    }

    [Fact]
    public async Task MemoryRecordToolCall_ParsesStatusEnum()
    {
        var toolCall = new ToolCall
        {
            ToolCallId = "tc-err", StepId = "step-1", ToolName = "fail_tool",
            ArgumentsJson = "{}", Status = ToolCallStatus.Error
        };
        _reasoningMemory.RecordToolCallAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<ToolCallStatus>(), Arg.Any<long?>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(toolCall);

        await AdvancedMemoryTools.MemoryRecordToolCall(
            _reasoningMemory, "step-1", "fail_tool", "{}", null, "Error");

        await _reasoningMemory.Received(1).RecordToolCallAsync(
            "step-1", "fail_tool", "{}", null, ToolCallStatus.Error,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    // ── memory_export_graph ──

    [Fact]
    public async Task MemoryExportGraph_ThrowsMcpExceptionWhenGraphQueryDisabled()
    {
        var options = Options.Create(new McpServerOptions { EnableGraphQuery = false });

        var act = () => AdvancedMemoryTools.MemoryExportGraph(_graphQueryService, options);

        await act.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task MemoryExportGraph_CallsQueryAsyncForNodesAndRelationships()
    {
        var options = Options.Create(new McpServerOptions { EnableGraphQuery = true });
        _graphQueryService.QueryAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());

        await AdvancedMemoryTools.MemoryExportGraph(_graphQueryService, options);

        await _graphQueryService.Received(2).QueryAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryExportGraph_ReturnsJsonWithNodeAndRelationshipCount()
    {
        var options = Options.Create(new McpServerOptions { EnableGraphQuery = true });
        _graphQueryService.QueryAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());

        var result = await AdvancedMemoryTools.MemoryExportGraph(_graphQueryService, options);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("nodeCount").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("relationshipCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task MemoryExportGraph_SessionScoped_UsesStoredSchemaPropertyNames()
    {
        // cycle-3: the stored schema uses snake_case `session_id` and `id` (not `sessionId`/`entityId`).
        // The old camelCase names made a session-scoped export return zero nodes and null endpoint ids.
        var options = Options.Create(new McpServerOptions { EnableGraphQuery = true });
        var captured = new List<string>();
        _graphQueryService.QueryAsync(
                Arg.Do<string>(q => captured.Add(q)),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());

        await AdvancedMemoryTools.MemoryExportGraph(_graphQueryService, options, sessionId: "sess-1");

        captured.Should().HaveCount(2);
        // The $sessionId PARAMETER name is unchanged; only the PROPERTY accessor must be snake_case, and
        // node ids must use `id` (not `entityId`). Assert the broken property accessors are gone.
        captured.Should().NotContain(q => q.Contains(".sessionId"), "the stored key property is session_id");
        captured.Should().NotContain(q => q.Contains(".entityId"), "the stored node id property is `id`");
        captured.Should().Contain(q => q.Contains("n.session_id = $sessionId"));
        captured.Should().Contain(q => q.Contains("coalesce(a.id, elementId(a))") && q.Contains("a.session_id"));
    }

    // ── memory_find_duplicates ──

    [Fact]
    public async Task MemoryFindDuplicates_ThrowsMcpExceptionWhenGraphQueryDisabled()
    {
        var options = Options.Create(new McpServerOptions { EnableGraphQuery = false });

        var act = () => AdvancedMemoryTools.MemoryFindDuplicates(_graphQueryService, options);

        await act.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task MemoryFindDuplicates_CallsQueryAsyncWithThresholdParameter()
    {
        var options = Options.Create(new McpServerOptions { EnableGraphQuery = true });
        _graphQueryService.QueryAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());

        await AdvancedMemoryTools.MemoryFindDuplicates(_graphQueryService, options, threshold: 0.75);

        await _graphQueryService.Received(1).QueryAsync(
            Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, object?>?>(d => d != null && (double)d["threshold"]! == 0.75),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryFindDuplicates_UsesIdProperty_NotEntityId()
    {
        var options = Options.Create(new McpServerOptions { EnableGraphQuery = true });
        var captured = new List<string>();
        _graphQueryService.QueryAsync(
                Arg.Do<string>(q => captured.Add(q)),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());

        await AdvancedMemoryTools.MemoryFindDuplicates(_graphQueryService, options);

        captured.Should().ContainSingle();
        captured[0].Should().Contain("a.id AS entityAId").And.Contain("b.id AS entityBId");
        captured[0].Should().NotContain(".entityId", "entities store their logical id under `id`");
    }

    [Fact]
    public async Task MemoryFindDuplicates_ReturnsJsonWithPairCount()
    {
        var options = Options.Create(new McpServerOptions { EnableGraphQuery = true });
        _graphQueryService.QueryAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());

        var result = await AdvancedMemoryTools.MemoryFindDuplicates(_graphQueryService, options);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("pairCount").GetInt32().Should().Be(0);
    }

    // ── extract_and_persist ──

    [Fact]
    public async Task ExtractAndPersist_CallsExtractAndPersistAsyncWithBuiltMessage()
    {
        var options = Options.Create(new McpServerOptions { DefaultSessionId = "ses-default" });
        var extractionResult = new ExtractionResult
        {
            Entities = Array.Empty<ExtractedEntity>(),
            Facts = Array.Empty<ExtractedFact>(),
            Preferences = Array.Empty<ExtractedPreference>(),
            Relationships = Array.Empty<ExtractedRelationship>(),
            SourceMessageIds = new[] { "gen-id-1" }
        };
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(extractionResult);

        await AdvancedMemoryTools.ExtractAndPersist(
            _memoryService, _idGenerator, _clock, options,
            "Hello world", "my-session");

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r =>
                r.SessionId == "my-session" &&
                r.Messages.Count == 1 &&
                r.Messages[0].Content == "Hello world"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAndPersist_UsesDefaultSessionIdFromOptions()
    {
        var options = Options.Create(new McpServerOptions { DefaultSessionId = "ses-default" });
        var extractionResult = new ExtractionResult
        {
            Entities = Array.Empty<ExtractedEntity>(),
            Facts = Array.Empty<ExtractedFact>(),
            Preferences = Array.Empty<ExtractedPreference>(),
            Relationships = Array.Empty<ExtractedRelationship>(),
            SourceMessageIds = new[] { "gen-id-1" }
        };
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(extractionResult);

        await AdvancedMemoryTools.ExtractAndPersist(
            _memoryService, _idGenerator, _clock, options, "Test message");

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => r.SessionId == "ses-default"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAndPersist_ReturnsJsonWithExtractedCounts()
    {
        var options = Options.Create(new McpServerOptions { DefaultSessionId = "ses-default" });
        var extractionResult = new ExtractionResult
        {
            Entities = new[] { new ExtractedEntity { Name = "Alice", Type = "Person", Confidence = 0.9 } },
            Facts = Array.Empty<ExtractedFact>(),
            Preferences = Array.Empty<ExtractedPreference>(),
            Relationships = Array.Empty<ExtractedRelationship>(),
            SourceMessageIds = new[] { "gen-id-1" }
        };
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(extractionResult);

        var result = await AdvancedMemoryTools.ExtractAndPersist(
            _memoryService, _idGenerator, _clock, options, "Alice is a person.");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("entityCount").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("factCount").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("preferenceCount").GetInt32().Should().Be(0);
    }
}
