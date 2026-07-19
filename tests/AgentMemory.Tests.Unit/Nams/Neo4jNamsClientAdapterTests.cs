using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Nams;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Domain;
using AgentMemory.Nams.Observability;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class Neo4jNamsClientAdapterTests
{
    private static Neo4jNamsClientAdapter CreateAdapter(FakeHttpMessageHandler fake, string? apiKey = "nams_key")
    {
        var httpClient = new HttpClient(fake) { BaseAddress = new Uri("https://nams.test/v1/") };
        var options = Options.Create(new NamsOptions
        {
            Endpoint = new Uri("https://nams.test/v1/"),
            ApiKey = apiKey,
            MaxRetryAttempts = 2,
            InitialRetryDelay = TimeSpan.FromMilliseconds(1)
        });
        return new Neo4jNamsClientAdapter(httpClient, options, NullLogger<Neo4jNamsClientAdapter>.Instance, new NamsMetrics());
    }

    // A 200 OK whose body stream fails mid-read (e.g. a truncated/reset connection) -- used to regression-test
    // that this doesn't escape as a raw, unclassified exception (see NamsClientExceptionMapperTests too).
    private sealed class StreamFailsOnReadContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => throw new IOException("connection reset mid-read");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new IOException("connection reset mid-read");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CreateConversationAsync_Success_DeserializesConversation()
    {
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.Created,
            """{"id":"conv-1","workspaceId":"ws-1","userId":"user-1","metadata":{"title":"hi"}}"""));
        var adapter = CreateAdapter(fake);

        var conversation = await adapter.CreateConversationAsync("user-1", null, CancellationToken.None);

        conversation.Id.Should().Be("conv-1");
        conversation.WorkspaceId.Should().Be("ws-1");
        conversation.UserId.Should().Be("user-1");
        conversation.Metadata!["title"].Should().Be("hi");
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/conversations"));
    }

    [Fact]
    public async Task CreateConversationAsync_NullUserIdAndMetadata_OmitsThoseFieldsEntirely()
    {
        // Regression test: without an explicit JsonIgnoreCondition, null userId/metadata would serialize as
        // explicit JSON nulls in the POST body rather than omitting the keys.
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.Created,
            """{"id":"conv-1","workspaceId":"ws-1"}"""));
        var adapter = CreateAdapter(fake);

        await adapter.CreateConversationAsync(userId: null, metadata: null, CancellationToken.None);

        var requestBody = await fake.Requests.Single().Content!.ReadAsStringAsync();
        using var requestJson = System.Text.Json.JsonDocument.Parse(requestBody);
        requestJson.RootElement.TryGetProperty("userId", out _).Should().BeFalse("userId: null must omit the key entirely");
        requestJson.RootElement.TryGetProperty("metadata", out _).Should().BeFalse("metadata: null must omit the key entirely");
    }

    [Fact]
    public async Task CreateConversationAsync_ServerError_DoesNotRetry_ThrowsMappedException()
    {
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var adapter = CreateAdapter(fake);

        var act = () => adapter.CreateConversationAsync("user-1", null, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NamsOperationException>();
        exception.Which.FailureKind.Should().Be(NamsFailureKind.ServerError);
        fake.Requests.Should().HaveCount(1); // write -- no idempotency key mechanism yet, so no retry
    }

    [Fact]
    public async Task GetContextAsync_Success_DeserializesAllThreeTiers()
    {
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """
            {
              "reflections": [{"id":"r1","content":"insight","sourceObsIds":["o1"]}],
              "observations": [{"id":"o1","content":"summary","sourceMsgIds":["m1"]}],
              "recentMessages": [{"id":"m1","content":"hello","role":"user","score":0.9,"tokenCount":2}]
            }
            """));
        var adapter = CreateAdapter(fake);

        var context = await adapter.GetContextAsync("conv-1", CancellationToken.None);

        context.Reflections.Single().Content.Should().Be("insight");
        context.Observations.Single().SourceMessageIds.Should().ContainSingle("o1");
        context.RecentMessages.Single().Score.Should().Be(0.9);
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/conversations/conv-1/context"));
        fake.Requests.Single().Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task GetContextAsync_TransientFailureThenSuccess_Retries()
    {
        var fake = new FakeHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => Json(HttpStatusCode.OK, """{"reflections":[],"observations":[],"recentMessages":[]}"""));
        var adapter = CreateAdapter(fake);

        var context = await adapter.GetContextAsync("conv-1", CancellationToken.None);

        context.RecentMessages.Should().BeEmpty();
        fake.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetContextAsync_SuccessResponseWithBodyStreamFailure_ThrowsMappedNetworkException()
    {
        // Regression test: a 200 OK whose body stream fails mid-read (truncated/reset connection) must be
        // mapped to a proper NamsOperationException(FailureKind.Network) -- not escape as a raw, unclassified
        // IOException that bypasses redaction and every caller's NamsOperationException-based handling.
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamFailsOnReadContent()
        });
        var adapter = CreateAdapter(fake);

        var act = () => adapter.GetContextAsync("conv-1", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NamsOperationException>();
        exception.Which.FailureKind.Should().Be(NamsFailureKind.Network);
    }

    [Fact]
    public async Task AddMessagesAsync_Success_ReturnsMessagesWithIds()
    {
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.Created, """
            {"messages":[{"id":"m1","conversationId":"conv-1","content":"hello","role":"user"}]}
            """));
        var adapter = CreateAdapter(fake);

        var messages = await adapter.AddMessagesAsync(
            "conv-1", [new NamsMessageInput("hello", "user")], CancellationToken.None);

        messages.Single().Id.Should().Be("m1");
        messages.Single().ConversationId.Should().Be("conv-1");
        var requestBody = await fake.Requests.Single().Content!.ReadAsStringAsync();
        requestBody.Should().Contain("\"content\":\"hello\"");
    }

    [Fact]
    public async Task AddMessagesAsync_ServerError_DoesNotRetry()
    {
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var adapter = CreateAdapter(fake);

        var act = () => adapter.AddMessagesAsync("conv-1", [new NamsMessageInput("hello", "user")], CancellationToken.None);

        await act.Should().ThrowAsync<NamsOperationException>();
        fake.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task SearchEntitiesAsync_Success_UsesRetryDespitePostVerb()
    {
        var fake = new FakeHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => Json(HttpStatusCode.OK, """{"entities":[{"id":"e1","name":"Acme"}],"searchType":"vector"}"""));
        var adapter = CreateAdapter(fake);

        var entities = await adapter.SearchEntitiesAsync("acme", null, 10, CancellationToken.None);

        entities.Single().Name.Should().Be("Acme");
        fake.Requests.Should().HaveCount(2); // search is a read despite the POST verb -- retries like other reads
    }

    [Fact]
    public async Task SearchEntitiesAsync_NullType_OmitsTypeFieldEntirely()
    {
        // Regression test: without an explicit JsonIgnoreCondition, a null type would serialize an explicit
        // JSON null in the POST body rather than omitting the key.
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """{"entities":[]}"""));
        var adapter = CreateAdapter(fake);

        await adapter.SearchEntitiesAsync("acme", type: null, 10, CancellationToken.None);

        var requestBody = await fake.Requests.Single().Content!.ReadAsStringAsync();
        using var requestJson = System.Text.Json.JsonDocument.Parse(requestBody);
        requestJson.RootElement.TryGetProperty("type", out _).Should().BeFalse("type: null must omit the key entirely");
    }

    [Fact]
    public async Task SearchMessagesAsync_Success_UsesRetryDespitePostVerb()
    {
        var fake = new FakeHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => Json(HttpStatusCode.OK, """
                {"messages":[{"id":"m1","content":"the quick brown fox","role":"user","score":0.8,"tokenCount":4}],"searchType":"vector"}
                """));
        var adapter = CreateAdapter(fake);

        var messages = await adapter.SearchMessagesAsync("conv-1", "quick brown fox", 5, CancellationToken.None);

        messages.Single().Content.Should().Be("the quick brown fox");
        messages.Single().Score.Should().Be(0.8);
        fake.Requests.Should().HaveCount(2); // search is a read despite the POST verb -- retries like other reads
        var requestBody = await fake.Requests[1].Content!.ReadAsStringAsync();
        requestBody.Should().Contain("\"query\":\"quick brown fox\"").And.Contain("\"limit\":5");
    }

    [Fact]
    public async Task DeleteConversationAsync_Success_SendsDeleteToCorrectPath()
    {
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """{"status":"deleted"}"""));
        var adapter = CreateAdapter(fake);

        await adapter.DeleteConversationAsync("conv-1", CancellationToken.None);

        fake.Requests.Single().Method.Should().Be(HttpMethod.Delete);
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/conversations/conv-1"));
    }

    [Fact]
    public async Task DeleteConversationAsync_TransientFailureThenSuccess_Retries()
    {
        var fake = new FakeHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => Json(HttpStatusCode.OK, """{"status":"deleted"}"""));
        var adapter = CreateAdapter(fake);

        var act = () => adapter.DeleteConversationAsync("conv-1", CancellationToken.None);

        await act.Should().NotThrowAsync();
        fake.Requests.Should().HaveCount(2); // idempotent -- confirmed live that deleting twice still succeeds
    }

    [Fact]
    public async Task ListEntitiesAsync_Success_DeserializesEntities_NoQueryInRequest()
    {
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK,
            """{"entities":[{"id":"e1","name":"Acme"}]}"""));
        var adapter = CreateAdapter(fake);

        var entities = await adapter.ListEntitiesAsync(1, CancellationToken.None);

        entities.Single().Name.Should().Be("Acme");
        fake.Requests.Single().Method.Should().Be(HttpMethod.Get);
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/entities?limit=1"));
    }

    [Fact]
    public async Task ListConversationsAsync_Success_DeserializesConversationSummaries()
    {
        // Shape confirmed live (Phase 10e probe) -- note the deliberate absence of "workspaceId", unlike
        // CreateConversationAsync's response.
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """
            {"conversations":[{"id":"conv-1","userId":"user-1","metadata":{"title":"hi"},"title":"hi",
            "firstMessageSnippet":"hello there","messageCount":4,"createdAt":"2026-07-17T22:37:19.781Z",
            "updatedAt":"2026-07-17T22:37:22.028Z"}]}
            """));
        var adapter = CreateAdapter(fake);

        var conversations = await adapter.ListConversationsAsync(50, CancellationToken.None);

        var summary = conversations.Single();
        summary.Id.Should().Be("conv-1");
        summary.UserId.Should().Be("user-1");
        summary.Title.Should().Be("hi");
        summary.FirstMessageSnippet.Should().Be("hello there");
        summary.MessageCount.Should().Be(4);
        summary.CreatedAt.Should().Be("2026-07-17T22:37:19.781Z");
        summary.UpdatedAt.Should().Be("2026-07-17T22:37:22.028Z");
        fake.Requests.Single().Method.Should().Be(HttpMethod.Get);
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/conversations?limit=50"));
    }

    [Fact]
    public async Task GetObservationsAsync_Success_DeserializesObservations()
    {
        // Shape confirmed live (Phase 10e probe): id/content/conversationId/createdAt/sourceMsgIds.
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """
            {"observations":[{"id":"o1","content":"summary of messages 10-29","conversationId":"conv-1",
            "createdAt":"2026-07-18T12:26:02.143Z","sourceMsgIds":["m10","m11"]}]}
            """));
        var adapter = CreateAdapter(fake);

        var observations = await adapter.GetObservationsAsync("conv-1", 50, CancellationToken.None);

        var observation = observations.Single();
        observation.Id.Should().Be("o1");
        observation.Content.Should().Be("summary of messages 10-29");
        observation.ConversationId.Should().Be("conv-1");
        observation.CreatedAt.Should().Be("2026-07-18T12:26:02.143Z");
        observation.SourceMessageIds.Should().Equal("m10", "m11");
        fake.Requests.Single().Method.Should().Be(HttpMethod.Get);
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/conversations/conv-1/observations?limit=50"));
    }

    [Fact]
    public async Task SetEntityFeedbackAsync_Success_SendsPutWithBodyAndDeserializesResult()
    {
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """{"id":"e1","updated":true}"""));
        var adapter = CreateAdapter(fake);

        var result = await adapter.SetEntityFeedbackAsync("e1", 0.9, true, CancellationToken.None);

        result.Id.Should().Be("e1");
        result.Updated.Should().BeTrue();
        fake.Requests.Single().Method.Should().Be(HttpMethod.Put);
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/entities/e1/feedback"));
        var requestBody = await fake.Requests.Single().Content!.ReadAsStringAsync();
        requestBody.Should().Contain("\"userScore\":0.9").And.Contain("\"confirmed\":true");
    }

    [Fact]
    public async Task SetEntityFeedbackAsync_WithOneNullArgument_OmitsThatFieldEntirely()
    {
        // The interface doc comment promises "pass null to leave either unset". Without an explicit
        // JsonIgnoreCondition, System.Text.Json would instead serialize an explicit "confirmed":null, which a
        // PUT full-value-replacement handler could plausibly read as "clear this field" -- this test locks in
        // the actual, safer behavior: the key is genuinely absent, not null.
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """{"id":"e1","updated":true}"""));
        var adapter = CreateAdapter(fake);

        await adapter.SetEntityFeedbackAsync("e1", userScore: 0.5, confirmed: null, CancellationToken.None);

        var requestBody = await fake.Requests.Single().Content!.ReadAsStringAsync();
        using var requestJson = System.Text.Json.JsonDocument.Parse(requestBody);
        requestJson.RootElement.GetProperty("userScore").GetDouble().Should().Be(0.5);
        requestJson.RootElement.TryGetProperty("confirmed", out _).Should().BeFalse(
            "confirmed: null must omit the key entirely, not serialize an explicit JSON null");
    }

    [Fact]
    public async Task SetEntityFeedbackAsync_TransientFailureThenSuccess_Retries()
    {
        var fake = new FakeHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => Json(HttpStatusCode.OK, """{"id":"e1","updated":true}"""));
        var adapter = CreateAdapter(fake);

        var result = await adapter.SetEntityFeedbackAsync("e1", 0.9, true, CancellationToken.None);

        result.Updated.Should().BeTrue();
        // A PUT full-value replacement is genuinely idempotent -- retries like DeleteConversationAsync's PUT/DELETE.
        fake.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetEntityGraphAsync_Success_DeserializesNodesAndEdges()
    {
        // Shape confirmed live (Phase 10e probe): nodes are flat NamsEntity records; edges carry a compound id.
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """
            {"nodes":[{"id":"e1","name":"Acme","type":"Org","confidence":0.9}],
             "edges":[{"id":"e1|RELATED_TO|e2","sourceId":"e1","targetId":"e2","type":"RELATED_TO",
             "confidence":0.8,"method":"gliner2","predicate":"located_at","sourceMessageCount":3}]}
            """));
        var adapter = CreateAdapter(fake);

        var graph = await adapter.GetEntityGraphAsync(CancellationToken.None);

        graph.Nodes.Single().Name.Should().Be("Acme");
        var edge = graph.Edges.Single();
        edge.SourceId.Should().Be("e1");
        edge.TargetId.Should().Be("e2");
        edge.Predicate.Should().Be("located_at");
        edge.SourceMessageCount.Should().Be(3);
        fake.Requests.Single().Method.Should().Be(HttpMethod.Get);
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/entities/graph"));
    }

    [Fact]
    public async Task ExpandGraphAsync_Success_DeserializesGenericNodesAndTruncation()
    {
        // Shape confirmed live (Phase 10e probe): expand nodes are NOT flat NamsEntity records -- they carry
        // labels + a heterogeneous properties bag (a Message node was observed live alongside Entity nodes).
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """
            {"nodes":[{"id":"m1","labels":["Message"],"properties":{"content":"hi","role":"user"}}],
             "edges":[{"id":"m1|MENTIONS|e1","sourceId":"m1","targetId":"e1","type":"MENTIONS"}],
             "truncated":{"nodeId":"e1","shown":1,"total":1}}
            """));
        var adapter = CreateAdapter(fake);

        var expansion = await adapter.ExpandGraphAsync("e1", ["e1"], CancellationToken.None);

        var node = expansion.Nodes.Single();
        node.Id.Should().Be("m1");
        node.Labels.Should().Equal("Message");
        node.Properties["content"].GetString().Should().Be("hi");
        expansion.Edges.Single().Type.Should().Be("MENTIONS");
        expansion.Truncated!.Shown.Should().Be(1);
        expansion.Truncated.Total.Should().Be(1);
        fake.Requests.Single().Method.Should().Be(HttpMethod.Post);
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/graph/expand"));
        var requestBody = await fake.Requests.Single().Content!.ReadAsStringAsync();
        requestBody.Should().Contain("\"nodeId\":\"e1\"").And.Contain("\"loadedIds\":[\"e1\"]");
    }

    [Fact]
    public async Task ExpandGraphAsync_TransientFailureThenSuccess_Retries()
    {
        var fake = new FakeHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => Json(HttpStatusCode.OK, """{"nodes":[],"edges":[],"truncated":null}"""));
        var adapter = CreateAdapter(fake);

        var expansion = await adapter.ExpandGraphAsync("e1", ["e1"], CancellationToken.None);

        expansion.Nodes.Should().BeEmpty();
        fake.Requests.Should().HaveCount(2); // read-only POST -- retries like SearchEntitiesAsync/SearchMessagesAsync
    }

    [Fact]
    public async Task RecordReasoningStepAsync_Success_SendsPostAndDeserializesStep()
    {
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.Created, """
            {"id":"s1","conversationId":"conv-1","reasoning":"thinking","actionTaken":"search","result":"found it"}
            """));
        var adapter = CreateAdapter(fake);

        var step = await adapter.RecordReasoningStepAsync("conv-1", "thinking", "search", "found it", CancellationToken.None);

        step.Id.Should().Be("s1");
        step.ConversationId.Should().Be("conv-1");
        step.Reasoning.Should().Be("thinking");
        step.ActionTaken.Should().Be("search");
        step.Result.Should().Be("found it");
        fake.Requests.Single().Method.Should().Be(HttpMethod.Post);
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/reasoning/steps"));
    }

    [Fact]
    public async Task RecordReasoningStepAsync_NullResult_OmitsResultFieldEntirely()
    {
        // Regression test: without an explicit JsonIgnoreCondition, a null `result` would serialize as an
        // explicit "result":null in the POST body rather than omitting the key -- this and RecordToolCallAsync
        // below were the only two request bodies in this push missing the annotation their siblings
        // (EntityFeedbackRequestBody, ExecuteCypherQueryRequestBody, CreateEntityRequestBody) already carry.
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.Created, """
            {"id":"s1","conversationId":"conv-1","reasoning":"thinking","actionTaken":"search"}
            """));
        var adapter = CreateAdapter(fake);

        await adapter.RecordReasoningStepAsync("conv-1", "thinking", "search", null, CancellationToken.None);

        var requestBody = await fake.Requests.Single().Content!.ReadAsStringAsync();
        using var requestJson = System.Text.Json.JsonDocument.Parse(requestBody);
        requestJson.RootElement.TryGetProperty("result", out _).Should().BeFalse(
            "result: null must omit the key entirely, not serialize an explicit JSON null");
    }

    [Fact]
    public async Task RecordReasoningStepAsync_ServerError_DoesNotRetry()
    {
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var adapter = CreateAdapter(fake);

        var act = () => adapter.RecordReasoningStepAsync("conv-1", "thinking", "search", null, CancellationToken.None);

        await act.Should().ThrowAsync<NamsOperationException>();
        fake.Requests.Should().HaveCount(1); // a genuine write -- resending would create a duplicate step
    }

    [Fact]
    public async Task ListReasoningStepsAsync_Success_DeserializesStepsAndUsesConversationIdQueryParam()
    {
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """
            {"steps":[{"id":"s1","reasoning":"thinking","actionTaken":"search","createdAt":"2026-07-18T12:26:02.143Z"}]}
            """));
        var adapter = CreateAdapter(fake);

        var steps = await adapter.ListReasoningStepsAsync("conv-1", CancellationToken.None);

        steps.Single().Id.Should().Be("s1");
        steps.Single().CreatedAt.Should().Be("2026-07-18T12:26:02.143Z");
        fake.Requests.Single().Method.Should().Be(HttpMethod.Get);
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/reasoning/steps?conversation_id=conv-1"));
    }

    [Fact]
    public async Task RecordToolCallAsync_Success_SendsPostWithPreSerializedInputOutput()
    {
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.Created,
            """{"id":"t1","stepId":"s1","toolName":"web_search","status":"success"}"""));
        var adapter = CreateAdapter(fake);

        var toolCall = await adapter.RecordToolCallAsync(
            "s1", "web_search", "{\"query\":\"foo\"}", "{\"results\":[\"r1\"]}", "success", 150, CancellationToken.None);

        toolCall.Id.Should().Be("t1");
        toolCall.StepId.Should().Be("s1");
        toolCall.ToolName.Should().Be("web_search");
        toolCall.Status.Should().Be("success");
        var requestBody = await fake.Requests.Single().Content!.ReadAsStringAsync();
        using var requestJson = System.Text.Json.JsonDocument.Parse(requestBody);
        requestJson.RootElement.GetProperty("input").GetString().Should().Be("{\"query\":\"foo\"}",
            "input must be a pre-serialized JSON string value, not a nested object");
        requestJson.RootElement.GetProperty("durationMs").GetInt32().Should().Be(150);
    }

    [Fact]
    public async Task RecordToolCallAsync_AllOptionalArgumentsNull_OmitsThoseFieldsEntirely()
    {
        // Regression test: stepId/output/status/durationMs are all optional -- without an explicit
        // JsonIgnoreCondition on each, passing null would serialize explicit JSON nulls rather than omitting
        // the keys (same class of gap as RecordReasoningStepAsync's `result` above).
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.Created,
            """{"id":"t1","toolName":"web_search","status":"success"}"""));
        var adapter = CreateAdapter(fake);

        await adapter.RecordToolCallAsync(
            stepId: null, "web_search", "{\"query\":\"foo\"}", output: null, status: null, durationMs: null, CancellationToken.None);

        var requestBody = await fake.Requests.Single().Content!.ReadAsStringAsync();
        using var requestJson = System.Text.Json.JsonDocument.Parse(requestBody);
        requestJson.RootElement.TryGetProperty("stepId", out _).Should().BeFalse("stepId: null must omit the key entirely");
        requestJson.RootElement.TryGetProperty("output", out _).Should().BeFalse("output: null must omit the key entirely");
        requestJson.RootElement.TryGetProperty("status", out _).Should().BeFalse("status: null must omit the key entirely");
        requestJson.RootElement.TryGetProperty("durationMs", out _).Should().BeFalse("durationMs: null must omit the key entirely");
    }

    [Fact]
    public async Task GetReasoningTraceAsync_Success_DeserializesFlatStepsAndToolCalls()
    {
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """
            {"conversationId":"conv-1",
             "steps":[{"id":"s1","reasoning":"thinking","actionTaken":"search","createdAt":"2026-07-18T12:26:02.143Z"}],
             "toolCalls":[{"id":"t1","stepId":"s1","toolName":"web_search","status":"success","input":"{}",
             "output":"{}","durationMs":42,"createdAt":"2026-07-18T12:26:03.729Z"}]}
            """));
        var adapter = CreateAdapter(fake);

        var trace = await adapter.GetReasoningTraceAsync("conv-1", CancellationToken.None);

        trace.ConversationId.Should().Be("conv-1");
        trace.Steps.Single().Id.Should().Be("s1");
        trace.ToolCalls.Single().StepId.Should().Be("s1");
        trace.ToolCalls.Single().DurationMs.Should().Be(42);
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/reasoning/trace/conv-1"));
    }

    [Fact]
    public async Task GetEntityProvenanceAsync_Success_DeserializesEnvelopeWithProvenanceField()
    {
        // Confirmed live (Phase 10e): the field is named "provenance", not "steps" as the pinned OpenAPI
        // snapshot's schema name wrongly implied.
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """{"entityId":"e1","provenance":[]}"""));
        var adapter = CreateAdapter(fake);

        var provenance = await adapter.GetEntityProvenanceAsync("e1", CancellationToken.None);

        provenance.EntityId.Should().Be("e1");
        provenance.Provenance.Should().BeEmpty();
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/reasoning/provenance/e1"));
    }

    [Fact]
    public async Task ExecuteCypherQueryAsync_Success_SendsCypherAndParamsAndDeserializesResult()
    {
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """
            {"columns":["cnt"],"rows":[{"cnt":541}],
             "stats":{"nodesCreated":0,"nodesDeleted":0,"relationshipsCreated":0}}
            """));
        var adapter = CreateAdapter(fake);

        var result = await adapter.ExecuteCypherQueryAsync(
            "MATCH (n) WHERE n.id = $id RETURN count(n) AS cnt",
            new Dictionary<string, object?> { ["id"] = "e1" },
            CancellationToken.None);

        result.Columns.Should().Equal("cnt");
        result.Rows.Single()["cnt"].GetInt32().Should().Be(541);
        result.Stats!["nodesCreated"].GetInt32().Should().Be(0);
        fake.Requests.Single().Method.Should().Be(HttpMethod.Post);
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/query"));
        var requestBody = await fake.Requests.Single().Content!.ReadAsStringAsync();
        using var requestJson = System.Text.Json.JsonDocument.Parse(requestBody);
        requestJson.RootElement.GetProperty("cypher").GetString().Should().Contain("$id");
        requestJson.RootElement.GetProperty("params").GetProperty("id").GetString().Should().Be("e1");
    }

    [Fact]
    public async Task ExecuteCypherQueryAsync_WriteRejectedByServer_ThrowsValidationFailure()
    {
        // Confirmed live (Phase 10e): a real CREATE attempt against the live NAMS SaaS was rejected with
        // exactly this shape -- HTTP 400, {"error":"write operations are not permitted via this endpoint"}.
        var fake = new FakeHttpMessageHandler(() => Json(
            HttpStatusCode.BadRequest, """{"error":"write operations are not permitted via this endpoint"}"""));
        var adapter = CreateAdapter(fake);

        var act = () => adapter.ExecuteCypherQueryAsync("CREATE (n:Test) RETURN n", null, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NamsOperationException>();
        exception.Which.FailureKind.Should().Be(NamsFailureKind.Validation);
        fake.Requests.Should().HaveCount(1); // a 400 is not transient -- must not retry
    }

    [Fact]
    public async Task ExecuteCypherQueryAsync_NullParameters_OmitsParamsWithoutError()
    {
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """{"columns":[],"rows":[]}"""));
        var adapter = CreateAdapter(fake);

        var result = await adapter.ExecuteCypherQueryAsync("MATCH (n) RETURN n LIMIT 0", null, CancellationToken.None);

        result.Rows.Should().BeEmpty();
        var requestBody = await fake.Requests.Single().Content!.ReadAsStringAsync();
        using var requestJson = System.Text.Json.JsonDocument.Parse(requestBody);
        requestJson.RootElement.TryGetProperty("params", out _).Should().BeFalse(
            "a null parameters argument must genuinely omit the \"params\" key, not serialize an explicit " +
            "JSON null, matching this method's JsonIgnoreCondition.WhenWritingNull annotation");
    }

    [Fact]
    public async Task CreateEntityAsync_CreatedResolution_DeserializesFullEntityFields()
    {
        // Shape confirmed live (Phase 10j probe): richer than the pinned OpenAPI snapshot's EntityResponse
        // (adds ontologyVersionId/resolution/systemAdded/validationMode, none of which NamsCreateEntityResult
        // needs to model since they're not consumed).
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.Created, """
            {"description":"probe","id":"e1","name":"Acme","ontologyVersionId":"ov_1","resolution":"created",
             "systemAdded":true,"type":"Org","validationMode":"permissive"}
            """));
        var adapter = CreateAdapter(fake);

        var result = await adapter.CreateEntityAsync("Acme", "Org", "probe", CancellationToken.None);

        result.Id.Should().Be("e1");
        result.Resolution.Should().Be("created");
        result.Name.Should().Be("Acme");
        result.Type.Should().Be("Org");
        result.Description.Should().Be("probe");
        result.DuplicateOf.Should().BeNull();
        result.MergedInto.Should().BeNull();
        fake.Requests.Single().Method.Should().Be(HttpMethod.Post);
        fake.Requests.Single().RequestUri.Should().Be(new Uri("https://nams.test/v1/entities"));
        var requestBody = await fake.Requests.Single().Content!.ReadAsStringAsync();
        using var requestJson = System.Text.Json.JsonDocument.Parse(requestBody);
        requestJson.RootElement.GetProperty("name").GetString().Should().Be("Acme",
            "name and type must not be transposed -- both are plain strings, so nothing else would catch it");
        requestJson.RootElement.GetProperty("type").GetString().Should().Be("Org");
        requestJson.RootElement.GetProperty("description").GetString().Should().Be("probe");
    }

    [Fact]
    public async Task CreateEntityAsync_ReviewPendingResolution_DeserializesDuplicateOf()
    {
        // Confirmed live: a probable (not certain) duplicate still returns full entity fields, plus
        // duplicate_of (snake_case -- confirmed inconsistent with this same response's camelCase fields).
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.Created, """
            {"description":"probe","duplicate_of":"e0","id":"e1","name":"Acme","ontologyVersionId":"ov_1",
             "resolution":"review_pending","systemAdded":true,"type":"Org","validationMode":"permissive"}
            """));
        var adapter = CreateAdapter(fake);

        var result = await adapter.CreateEntityAsync("Acme", "Org", "probe", CancellationToken.None);

        result.Resolution.Should().Be("review_pending");
        result.Name.Should().Be("Acme");
        result.DuplicateOf.Should().Be("e0");
    }

    [Fact]
    public async Task CreateEntityAsync_MergedResolution_DeserializesMinimalShape_NoNameOrType()
    {
        // The genuinely surprising live discovery this phase's own live test caught: when NAMS auto-merges
        // the submission into an existing entity, the response is a COMPLETELY different, minimal shape --
        // no name/type/description/duplicate_of at all, only id/resolution/merged_into/confidence.
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.Created, """
            {"confidence":0.9413830497142857,"id":"e1","merged_into":"e0","resolution":"merged"}
            """));
        var adapter = CreateAdapter(fake);

        var result = await adapter.CreateEntityAsync("Acme", "Org", "probe", CancellationToken.None);

        result.Id.Should().Be("e1");
        result.Resolution.Should().Be("merged");
        result.MergedInto.Should().Be("e0");
        result.Confidence.Should().BeApproximately(0.9413830497142857, 1e-12);
        result.Name.Should().BeNull("the merged-resolution response genuinely omits name -- this is not a bug");
        result.Type.Should().BeNull();
        result.Description.Should().BeNull();
    }

    [Fact]
    public async Task CreateEntityAsync_ServerError_DoesNotRetry()
    {
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var adapter = CreateAdapter(fake);

        var act = () => adapter.CreateEntityAsync("Acme", "Org", null, CancellationToken.None);

        await act.Should().ThrowAsync<NamsOperationException>();
        fake.Requests.Should().HaveCount(1); // a genuine write -- resending would create a duplicate entity
    }

    [Fact]
    public async Task AnyOperation_CallerCancellation_PropagatesOperationCanceledException()
    {
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        var adapter = CreateAdapter(fake);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => adapter.GetContextAsync("conv-1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ServerError_RedactsApiKeyFromExceptionMessage()
    {
        const string apiKey = "nams_super_secret_12345";
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.InternalServerError, $$"""{"error":"bad key {{apiKey}}"}"""));
        var adapter = CreateAdapter(fake, apiKey);

        var act = () => adapter.GetContextAsync("conv-1", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NamsOperationException>();
        exception.Which.Message.Should().NotContain(apiKey);
    }

    [Fact]
    public async Task NetworkFailure_MapsToNetworkFailureKind()
    {
        var fake = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));
        var adapter = CreateAdapter(fake);

        var act = () => adapter.GetContextAsync("conv-1", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NamsOperationException>();
        exception.Which.FailureKind.Should().Be(NamsFailureKind.Network);
    }

    [Fact]
    public async Task NetworkFailure_RedactsApiKeyFromInnerExceptionToo()
    {
        // Regression test: FromTransportException redacted the outer NamsOperationException.Message, but
        // originally attached the raw, unredacted transport exception as InnerException -- a logger calling
        // Exception.ToString() (which recurses into InnerException) would still leak the secret. The full
        // ToString() chain, not just the top-level Message, must never contain it.
        const string apiKey = "nams_super_secret_12345";
        var fake = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException($"connection refused for key {apiKey}"));
        var adapter = CreateAdapter(fake, apiKey);

        var act = () => adapter.GetContextAsync("conv-1", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NamsOperationException>();
        exception.Which.ToString().Should().NotContain(apiKey);
        exception.Which.InnerException.Should().NotBeNull();
        exception.Which.InnerException!.Message.Should().NotContain(apiKey);
    }
}
