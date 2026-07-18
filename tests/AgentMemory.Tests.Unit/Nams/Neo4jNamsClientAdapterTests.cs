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
}
