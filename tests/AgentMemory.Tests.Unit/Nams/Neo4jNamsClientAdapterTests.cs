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
