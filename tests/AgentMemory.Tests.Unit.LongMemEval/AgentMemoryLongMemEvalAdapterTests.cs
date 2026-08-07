using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class AgentMemoryLongMemEvalAdapterTests
{
    [Fact]
    public async Task InvokeAsync_PersistsInjectedHistoryAndAnswersOnlyFromRecalledMemory()
    {
        var memory = Substitute.For<IMemoryService>();
        IReadOnlyList<Message>? stored = null;
        RecallRequest? recallRequest = null;
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                stored = call.Arg<IEnumerable<Message>>().ToArray();
                return stored;
            });
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                recallRequest = call.Arg<RecallRequest>();
                return new RecallResult
                {
                    Context = new MemoryContext
                    {
                        SessionId = recallRequest.SessionId,
                        AssembledAtUtc = DateTimeOffset.UnixEpoch,
                        RelevantMessages = new MemoryContextSection<Message>
                        {
                            Items =
                            [
                                Message(
                                    recallRequest.SessionId,
                                    "assistant",
                                    "Alice moved to Zurich in March.")
                            ]
                        }
                    },
                    TotalItemsRetrieved = 1
                };
            });

        var chat = Substitute.For<IChatClient>();
        IReadOnlyList<ChatMessage>? answerPrompt = null;
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                answerPrompt = call.Arg<IEnumerable<ChatMessage>>().ToArray();
                return new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, "Alice lives in Zurich."));
            });

        var adapter = new AgentMemoryLongMemEvalAdapter(memory, chat, "test-run");
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory(
        [
            ("Alice moved to Zurich in March.", "Thanks, I will remember that."),
            ("Her favorite color is blue.", "Understood.")
        ]);

        var response = await adapter.InvokeAsync("Where does Alice live?");

        response.Text.Should().Be("Alice lives in Zurich.");
        stored.Should().HaveCount(4);
        stored!.Select(message => message.SessionId).Distinct().Should().ContainSingle();
        recallRequest.Should().NotBeNull();
        recallRequest!.Options.BlendMode.Should().Be(RetrievalBlendMode.MemoryOnly);
        recallRequest.Options.MaxRecentMessages.Should().Be(0);
        recallRequest.Options.MaxEntities.Should().Be(0);
        answerPrompt.Should().NotBeNull();
        answerPrompt!.Select(message => message.Text).Should()
            .Contain(text => text!.Contains("Alice moved to Zurich", StringComparison.Ordinal));
        var telemetry = adapter.QuestionTelemetry.Should().ContainSingle().Subject;
        telemetry.Should().BeEquivalentTo(
                new LongMemEvalQuestionTelemetry(1, 4, 1, false)
                {
                    RawMessagesRetrieved = 1
                },
                options => options.Excluding(info => info.Path == "StageTimings"));
        telemetry.StageTimings.Should().NotBeNull(
            "accepted LongMemEval questions must expose a phase waterfall");
        telemetry.StageTimings!.StorageMs.Should().BeGreaterThan(0);
        telemetry.StageTimings.RetrievalMs.Should().BeGreaterThan(0);
        telemetry.StageTimings.AnswerMs.Should().BeGreaterThan(0);
        telemetry.StageTimings.ExtractionPersistenceMs.Should().Be(0);
        telemetry.StageTimings.GraphReadBackMs.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_RejectsAQuestionWithoutInjectedHistory()
    {
        var adapter = new AgentMemoryLongMemEvalAdapter(
            Substitute.For<IMemoryService>(),
            Substitute.For<IChatClient>(),
            "test-run");
        await adapter.ResetSessionAsync();

        var act = () => adapter.InvokeAsync("What should I remember?");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*history*");
    }

    [Fact]
    public async Task ResetSessionAsync_IsolatesQuestionsWithDistinctSessionAndOwnerScopes()
    {
        var memory = Substitute.For<IMemoryService>();
        var requests = new List<RecallRequest>();
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<RecallRequest>();
                requests.Add(request);
                return new RecallResult
                {
                    Context = new MemoryContext
                    {
                        SessionId = request.SessionId,
                        AssembledAtUtc = DateTimeOffset.UnixEpoch,
                        RelevantMessages = new MemoryContextSection<Message>
                        {
                            Items = [Message(request.SessionId, "user", request.Query)]
                        }
                    },
                    TotalItemsRetrieved = 1
                };
            });
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
        var adapter = new AgentMemoryLongMemEvalAdapter(memory, chat, "test-run");

        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory([("one", "first")]);
        await adapter.InvokeAsync("question one");
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory([("two", "second")]);
        await adapter.InvokeAsync("question two");

        requests.Should().HaveCount(2);
        requests.Select(request => request.SessionId).Distinct().Should().HaveCount(2);
        requests.Select(request => request.UserId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task InvokeAsync_RecordsEmptyRetrievalInTelemetry()
    {
        var memory = Substitute.For<IMemoryService>();
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<RecallRequest>();
                return new RecallResult
                {
                    Context = new MemoryContext
                    {
                        SessionId = request.SessionId,
                        AssembledAtUtc = DateTimeOffset.UnixEpoch,
                        RelevantMessages = new MemoryContextSection<Message>
                        {
                            Items = []
                        }
                    },
                    TotalItemsRetrieved = 0
                };
            });
        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory,
            Substitute.For<IChatClient>(),
            "test-run");
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory([("one", "first")]);

        var act = () => adapter.InvokeAsync("question one");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*retrieved no history*");
        adapter.QuestionTelemetry.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                QuestionNumber = 1,
                MessagesStored = 2,
                ItemsRetrieved = 0,
                RecallTruncated = false,
                Status = "retrieval-empty"
            });
    }

    [Fact]
    public async Task InvokeAsync_RecordsSanitizedAnswerFailureInTelemetry()
    {
        var memory = Substitute.For<IMemoryService>();
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<RecallRequest>();
                return new RecallResult
                {
                    Context = new MemoryContext
                    {
                        SessionId = request.SessionId,
                        AssembledAtUtc = DateTimeOffset.UnixEpoch,
                        RelevantMessages = new MemoryContextSection<Message>
                        {
                            Items = [Message(request.SessionId, "user", "remembered detail")]
                        }
                    },
                    TotalItemsRetrieved = 1
                };
            });
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ChatResponse>(
                new InvalidOperationException("provider-secret-detail")));
        var adapter = new AgentMemoryLongMemEvalAdapter(memory, chat, "test-run");
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory([("one", "first")]);

        var act = () => adapter.InvokeAsync("question one");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("LongMemEval answer stage failed.");
        adapter.QuestionTelemetry.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                QuestionNumber = 1,
                MessagesStored = 2,
                ItemsRetrieved = 1,
                RecallTruncated = false,
                Status = "answer-error"
            });
    }

    [Fact]
    public async Task InvokeAsync_RecordsEvidenceResolutionFailureBeforeStorage()
    {
        var entry = LongMemEvalEvidenceIndexTests.Entry();
        var options = LongMemEvalEvidenceIndexTests.Options();
        var history = AgentEval.Memory.External.LongMemEval.LongMemEvalHistoryFormatter.Format(entry, options);
        var memory = Substitute.For<IMemoryService>();
        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory,
            Substitute.For<IChatClient>(),
            "evidence-error-run",
            new LongMemEvalAdapterOptions
            {
                EvidenceIndex = LongMemEvalEvidenceIndex.Create([entry], options)
            });
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory(history);

        var act = () => adapter.InvokeAsync("wrong prompt");

        await act.Should().ThrowAsync<InvalidOperationException>();
        adapter.QuestionTelemetry.Should().ContainSingle().Which.Status.Should()
            .Be("evidence-resolution-error");
        await memory.DidNotReceive()
            .AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_EmitsRankedSourceEvidenceWithoutPersistingGoldLabels()
    {
        var entry = LongMemEvalEvidenceIndexTests.Entry();
        var benchmarkOptions = LongMemEvalEvidenceIndexTests.Options();
        var history = AgentEval.Memory.External.LongMemEval.LongMemEvalHistoryFormatter
            .Format(entry, benchmarkOptions);
        var evidenceIndex = LongMemEvalEvidenceIndex.Create([entry], benchmarkOptions);
        var memory = Substitute.For<IMemoryService>();
        IReadOnlyList<Message>? stored = null;
        RecallRequest? recallRequest = null;
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                stored = call.Arg<IEnumerable<Message>>().ToArray();
                return stored;
            });
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                recallRequest = call.Arg<RecallRequest>();
                var items = stored!;
                return new RecallResult
                {
                    Context = new MemoryContext
                    {
                        SessionId = recallRequest.SessionId,
                        AssembledAtUtc = DateTimeOffset.UnixEpoch,
                        RelevantMessages = new MemoryContextSection<Message>
                        {
                            Items = items,
                            RankedItems = items.Select((message, index) =>
                                new MemoryContextRankedItem(
                                    message.MessageId,
                                    0.99 - index / 100d,
                                    index + 1,
                                    index + 1)).ToArray()
                        }
                    },
                    TotalItemsRetrieved = items.Count
                };
            });
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "two weeks")));
        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory,
            chat,
            "evidence-run",
            new LongMemEvalAdapterOptions
            {
                EvidenceIndex = evidenceIndex,
                EvidenceDetail = LongMemEvalEvidenceDetail.Identifiers
            });
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory(history);

        var response = await adapter.InvokeAsync(LongMemEvalEvidenceIndexTests.InvocationPrompt(entry));

        recallRequest!.Options.IncludeDiagnostics.Should().BeTrue();
        // G3B.9: of this fixture's four injected messages, two are AgentEval's fabricated
        // session-boundary turn. Only the real conversation is persisted now, so the count moved
        // 4 -> 2. The assertion is strengthened rather than merely relaxed: the fabricated pair must
        // be provably absent, not just uncounted.
        stored.Should().HaveCount(2);
        stored!.Should().NotContain(message =>
            message.Content.Contains("Understood.", StringComparison.Ordinal) ||
            message.Content.StartsWith("--- Session", StringComparison.Ordinal));
        stored.Should().OnlyContain(message =>
            message.Metadata.ContainsKey("sourceSessionId") &&
            !message.Metadata.ContainsKey("hasAnswer") &&
            !message.Metadata.ContainsKey("answerSessionIds"));
        var telemetry = adapter.QuestionTelemetry.Should().ContainSingle().Subject;
        telemetry.QuestionId.Should().Be("q-1");
        telemetry.RetrievalEvidence.Should().NotBeNull();
        telemetry.RetrievalEvidence!.GoldSessionRecallAtK.Should().Be(1);
        telemetry.RetrievalEvidence.GoldTurnHitAtK.Should().BeTrue();
        telemetry.RetrievalEvidence.RankedItems.Should()
            .OnlyContain(item => item.Content == null);
        var evidenceKey =
            AgentEval.Memory.External.Models.QuestionEvidenceEnvelope.AdditionalPropertiesKey;
        response.AdditionalProperties.Should().ContainKey(evidenceKey);
        var normalized = response.AdditionalProperties![evidenceKey].Should()
            .BeOfType<AgentEval.Memory.External.Models.QuestionEvidenceEnvelope>().Subject;
        // Follows the storage change: the stubbed recall echoes what was persisted, and the
        // fabricated boundary turn is no longer persisted.
        normalized.Retrieved.Should().HaveCount(2);
        normalized.AnswerContext.Should().HaveCount(2);
        normalized.Retrieved.Should().OnlyContain(item => item.Content == null);
        normalized.AnswerContext.Should().OnlyContain(item => item.Content == null);
        normalized.AnswerContext.Select(item => item.AnswerContextOrder).Should().Equal(1, 2);
    }

    [Fact]
    public async Task InvokeAsync_StructuredModeExtractsBySourceSessionAndExcludesRawRecall()
    {
        var entry = LongMemEvalEvidenceIndexTests.Entry();
        var benchmarkOptions = LongMemEvalEvidenceIndexTests.Options();
        var history = AgentEval.Memory.External.LongMemEval.LongMemEvalHistoryFormatter
            .Format(entry, benchmarkOptions);
        var evidenceIndex = LongMemEvalEvidenceIndex.Create([entry], benchmarkOptions);
        var memory = Substitute.For<IMemoryService>();
        RecallRequest? recallRequest = null;
        ExtractionRequest? extractionRequest = null;
        var extractionProgress = new List<(int Completed, int Total)>();
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.ExtractAndPersistAsync(
                Arg.Any<ExtractionRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                extractionRequest = call.Arg<ExtractionRequest>();
                return new ExtractionResult();
            });
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                recallRequest = call.Arg<RecallRequest>();
                return new RecallResult
                {
                    Context = new MemoryContext
                    {
                        SessionId = recallRequest.SessionId,
                        AssembledAtUtc = DateTimeOffset.UnixEpoch,
                        RelevantFacts = new MemoryContextSection<Fact>
                        {
                            Items =
                            [
                                new Fact
                                {
                                    FactId = "fact-1",
                                    Subject = "user",
                                    Predicate = "stayed_in",
                                    Object = "Japan for two weeks",
                                    Confidence = 0.95,
                                    CreatedAtUtc = DateTimeOffset.UnixEpoch
                                }
                            ]
                        }
                    },
                    TotalItemsRetrieved = 1
                };
            });
        var chat = Substitute.For<IChatClient>();
        IReadOnlyList<ChatMessage>? answerMessages = null;
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                answerMessages = call.Arg<IEnumerable<ChatMessage>>().ToArray();
                return new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, "The stay lasted two weeks."));
            });
        var adapterOptions = new LongMemEvalAdapterOptions
        {
            EvidenceIndex = evidenceIndex,
            EvidenceDetail = LongMemEvalEvidenceDetail.Identifiers,
            ExtractionProgress = (completed, total) => extractionProgress.Add((completed, total))
        };
        var modeProperty = typeof(LongMemEvalAdapterOptions).GetProperty("MemoryMode");
        modeProperty.Should().NotBeNull(
            "G3A requires an explicit raw/structured/hybrid operating-mode switch");
        modeProperty!.SetValue(
            adapterOptions,
            Enum.Parse(modeProperty.PropertyType, "Structured"));
        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory, chat, "structured-run", adapterOptions);
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory(history);

        await adapter.InvokeAsync(LongMemEvalEvidenceIndexTests.InvocationPrompt(entry));

        await memory.Received(1).ExtractAndPersistAsync(
            Arg.Any<ExtractionRequest>(),
            Arg.Any<CancellationToken>());
        extractionProgress.Should().Equal((0, 1), (1, 1));
        extractionRequest.Should().NotBeNull();
        extractionRequest!.UserId.Should().NotBeNull();
        extractionRequest.Messages.Should().HaveCount(2);
        var syntheticBoundaries = extractionRequest.Messages.Select(message =>
            message.Metadata.TryGetValue("sourceSyntheticBoundary", out var boundary) &&
            Equals(boundary, true));
        syntheticBoundaries.Should().OnlyContain(isSynthetic => !isSynthetic);
        recallRequest.Should().NotBeNull();
        recallRequest!.Options.MaxRelevantMessages.Should().Be(0);
        recallRequest.Options.MaxEntities.Should().Be(10);
        recallRequest.Options.MaxFacts.Should().Be(10);
        recallRequest.Options.MaxPreferences.Should().Be(10);
        answerMessages.Should().Contain(message =>
            message.Text != null &&
            message.Text.Contains("[fact] user stayed_in Japan for two weeks", StringComparison.Ordinal));
        var telemetry = adapter.QuestionTelemetry.Should().ContainSingle().Subject;
        var extractionUnitsProperty = telemetry.GetType().GetProperty("ExtractionUnits");
        extractionUnitsProperty.Should().NotBeNull(
            "structured-mode telemetry must expose the extraction work performed");
        extractionUnitsProperty!.GetValue(telemetry).Should().Be(1);
    }

    [Fact]
    public async Task InvokeAsync_PreparedStructuredModeSkipsWritesAndExtraction()
    {
        var entry = LongMemEvalEvidenceIndexTests.Entry();
        var benchmarkOptions = LongMemEvalEvidenceIndexTests.Options();
        var history = AgentEval.Memory.External.LongMemEval.LongMemEvalHistoryFormatter
            .Format(entry, benchmarkOptions);
        var invocationPrompt = LongMemEvalEvidenceIndexTests.InvocationPrompt(entry);
        var evidenceIndex = LongMemEvalEvidenceIndex.Create([entry], benchmarkOptions);
        var evidenceQuestion = evidenceIndex.GetByQuestionId(entry.QuestionId);
        var sourceSessions = evidenceQuestion.Messages
            .Where(message =>
                !message.IsSyntheticBoundary &&
                !message.IsSyntheticFormatterPadding)
            .Select(message => message.SourceSessionOrdinal)
            .Distinct()
            .Count();
        var graphSnapshot = new LongMemEvalGraphSnapshot(1, 1, 1, 1, 1, 3, 3, 6, 2);
        var manifest = LongMemEvalPreparationManifest.Create(
            "prepared-test",
            "dataset-sha256",
            "agenteval-revision",
            "prepared-run",
            "answer-model",
            "judge-model",
            "extraction-model",
            "embedding-model",
            1536,
            30,
            "source-message-time",
            [
                new LongMemEvalPreparedQuestion(
                    1, evidenceQuestion.QuestionId, LongMemEvalEvidenceIndex.Fingerprint(history),
                    LongMemEvalPreparationManifest.Hash(
                        "prepared-run-session-0001|prepared-run-owner-0001"),
                    evidenceQuestion.Messages.Count(m =>
                        !m.IsSyntheticBoundary && !m.IsSyntheticFormatterPadding),
                    sourceSessions, sourceSessions, graphSnapshot)
            ],
            sourceSessions * 4);
        var memory = Substitute.For<IMemoryService>();
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new RecallResult
            {
                Context = new MemoryContext
                {
                    SessionId = call.Arg<RecallRequest>().SessionId,
                    AssembledAtUtc = DateTimeOffset.UnixEpoch,
                    RelevantFacts = new MemoryContextSection<Fact>
                    {
                        Items =
                        [
                            new Fact
                            {
                                FactId = "fact-prepared",
                                Subject = "user",
                                Predicate = "stayed_in",
                                Object = "Japan for two weeks",
                                Confidence = 0.95,
                                CreatedAtUtc = DateTimeOffset.UnixEpoch
                            }
                        ]
                    }
                },
                TotalItemsRetrieved = 1
            });
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "The stay lasted two weeks.")));
        var graphProbe = new PreparedGraphProbe(graphSnapshot);
        var adapterOptions = new LongMemEvalAdapterOptions
        {
            MemoryMode = LongMemEvalMemoryMode.Structured,
            EvidenceIndex = evidenceIndex,
            EvidenceDetail = LongMemEvalEvidenceDetail.Identifiers,
            RequireGraphReadBack = true,
            GraphProbe = graphProbe,
            ModelId = "answer-model",
            PreparedState = new LongMemEvalPreparedState(manifest, "prepared-run")
        };
        var preparedProperty = typeof(LongMemEvalAdapterOptions).GetProperty("PreparedMemory");
        preparedProperty.Should().NotBeNull(
            "prepared evaluation must be an explicit, reportable operating mode");
        preparedProperty!.SetValue(adapterOptions, true);
        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory, chat, "prepared-run", adapterOptions);
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory(history);

        await adapter.InvokeAsync(invocationPrompt);

        await memory.DidNotReceive().AddMessagesAsync(
            Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>());
        await memory.DidNotReceive().ExtractAndPersistAsync(
            Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>());
        await memory.Received(1).RecallAsync(
            Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
        var telemetry = adapter.QuestionTelemetry.Should().ContainSingle().Subject;
        telemetry.MessagesStored.Should().Be(0);
        telemetry.ExtractionUnits.Should().Be(0);
        // Preparation persists real conversation only; the fabricated boundary turns are excluded.
        telemetry.MessagesPrepared.Should().Be(evidenceQuestion.Messages.Count(m =>
            !m.IsSyntheticBoundary && !m.IsSyntheticFormatterPadding));
        telemetry.ExtractionUnitsPrepared.Should().Be(sourceSessions);
        telemetry.PreparedMemory.Should().BeTrue();
        telemetry.StageTimings.Should().NotBeNull();
        telemetry.StageTimings!.StorageMs.Should().Be(0);
        telemetry.StageTimings.ExtractionPersistenceMs.Should().Be(0);
    }

    private sealed class PreparedGraphProbe(LongMemEvalGraphSnapshot snapshot) : ILongMemEvalGraphProbe
    {
        public Task<LongMemEvalGraphSnapshot> ReadAsync(
            string ownerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }
    private static Message Message(string sessionId, string role, string content) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        SessionId = sessionId,
        ConversationId = sessionId,
        Role = role,
        Content = content,
        TimestampUtc = DateTimeOffset.UnixEpoch
    };
}
