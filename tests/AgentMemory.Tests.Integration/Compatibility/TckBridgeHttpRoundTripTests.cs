using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.TckBridge;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AgentMemory.Tests.Integration.Compatibility;

/// <summary>
/// End-to-end proof that the Bronze TCK bridge (tools/AgentMemory.TckBridge) actually works as an HTTP
/// server, not just that its underlying services pass unit tests: this hosts the bridge's real
/// <c>Program</c> in-process via <see cref="WebApplicationFactory{TEntryPoint}"/> (TestServer — no real
/// port bound) wired to the SAME live Neo4j Testcontainer used by <see cref="TckMirroredBehaviorTests"/>,
/// then drives the full Bronze flow over real HTTP calls, asserting on the actual wire JSON responses
/// (the TCK <c>Tck*</c> DTOs — snake_case on the wire) rather than the underlying domain types.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
[Trait("Compatibility", "TCK-Bridge-HTTP")]
public sealed class TckBridgeHttpRoundTripTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private BridgeWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    // Mirrors Program.cs's ConfigureHttpJsonOptions (snake_case wire, case-insensitive) so requests we
    // serialize and responses we deserialize match the actual bridge contract byte-for-byte.
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // GuidIdGenerator.GenerateId() returns Guid.NewGuid().ToString("N") — 32 lowercase hex chars.
    private static readonly Regex ServerAssignedId = new(@"^[0-9a-fA-F]{32}$", RegexOptions.Compiled);

    // System.Text.Json's default DateTimeOffset converter writes the round-trip ("O") form, e.g.
    // 2026-07-11T12:34:56.7890123+00:00 — still ISO-8601 even though it uses a numeric offset, not "Z".
    private static readonly Regex Iso8601Timestamp = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$",
        RegexOptions.Compiled);

    public TckBridgeHttpRoundTripTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();
        _factory = new BridgeWebApplicationFactory(_fixture);
        _client = CreateClientPointedAtFixtureContainer();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    /// <summary>
    /// Builds the TestServer-backed <see cref="HttpClient"/>, redirecting the bridge's Neo4j config at
    /// the fixture's live container.
    /// </summary>
    /// <remarks>
    /// Program.cs reads Neo4j:*/EmbeddingDimensions off <c>builder.Configuration</c> into plain local
    /// variables immediately after <c>WebApplication.CreateBuilder(args)</c>, before <c>Build()</c> runs.
    /// <see cref="WebApplicationFactory{TEntryPoint}"/>'s <c>ConfigureWebHost</c>/<c>ConfigureAppConfiguration</c>
    /// overrides (see <see cref="BridgeWebApplicationFactory"/>) are only spliced in once <c>Build()</c>
    /// executes — verified empirically: with only that override in place, the bootstrapped bridge still
    /// tried to reach the hardcoded default <c>bolt://localhost:7687</c> instead of the fixture's
    /// container, i.e. too late for the locals Program.cs already captured. Environment variables ARE
    /// visible in time, because <c>CreateBuilder(args)</c>'s own <c>AddEnvironmentVariables()</c> source
    /// is added right at the start — before any of those local reads. So the actual override channel is
    /// process environment variables, scoped tightly around the one call that triggers the factory's
    /// lazy host build (and therefore Program.Main's execution): <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/>.
    /// </remarks>
    private HttpClient CreateClientPointedAtFixtureContainer()
    {
        var overrides = new (string Key, string Value)[]
        {
            ("Neo4j__Uri", _fixture.ConnectionString),
            ("Neo4j__Username", _fixture.User),
            ("Neo4j__Password", _fixture.Password),
            ("Neo4j__Database", "neo4j"),
            ("EmbeddingDimensions",
                Neo4jIntegrationFixture.TestEmbeddingDimensions.ToString(CultureInfo.InvariantCulture)),
        };
        var previous = overrides
            .Select(o => (o.Key, Previous: Environment.GetEnvironmentVariable(o.Key)))
            .ToList();
        foreach (var (key, value) in overrides)
            Environment.SetEnvironmentVariable(key, value);
        try
        {
            return _factory.CreateClient();
        }
        finally
        {
            foreach (var (key, previousValue) in previous)
                Environment.SetEnvironmentVariable(key, previousValue);
        }
    }

    [Fact]
    public async Task NET_TCK_HTTP_001_FullBronzeFlow_RoundTripsOverRealHttpAgainstLiveNeo4j()
    {
        var sessionId = $"session-{Guid.NewGuid():N}";

        // ---- POST /setup ----
        var setupResponse = await _client.PostAsync("/setup", content: null);
        setupResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // ---- POST /add_message (first call creates the conversation) ----
        var message1 = await AddMessageAsync(sessionId, "user", "Remember that the launch date is July 11.");
        AssertServerAssigned(message1);

        // ---- POST /add_message (second call, same session_id, must reuse the same conversation) ----
        var message2 = await AddMessageAsync(sessionId, "assistant", "Noted, I will remind you the day before.");
        AssertServerAssigned(message2);
        message2.Id.Should().NotBe(message1.Id, "each message gets its own server-assigned id");

        // ---- POST /get_conversation: insertion / oldest-first order ----
        var conversation = await GetConversationAsync(sessionId, limit: null);
        conversation.SessionId.Should().Be(sessionId);
        conversation.Messages.Select(m => m.Id).Should().Equal(
            new[] { message1.Id, message2.Id },
            "get_conversation must return messages oldest-first, matching insertion order — proving the " +
            "second add_message reused the conversation the first one created rather than starting a new one");
        conversation.Messages.Select(m => m.Content).Should().Equal(
            "Remember that the launch date is July 11.",
            "Noted, I will remind you the day before.");

        // A third message, then confirm Limit truncates the chronological (oldest-first) list.
        var message3 = await AddMessageAsync(sessionId, "user", "Also block my calendar for that morning.");
        var limited = await GetConversationAsync(sessionId, limit: 2);
        // Program.cs applies Take(limit) to the oldest-first GetAllSessionMessagesAsync result, so
        // Limit=2 keeps the two OLDEST messages and drops the newest one — not a "most recent N" cap.
        limited.Messages.Select(m => m.Id).Should().Equal(new[] { message1.Id, message2.Id });

        // ---- POST /search_messages ----
        // StubEmbeddingGenerator derives its vector deterministically from the exact input text, so
        // querying with message1's exact content reproduces message1's exact embedding — guaranteed
        // top (score 1.0) match regardless of what the other messages' random vectors happen to be.
        var searchResults = await SearchMessagesAsync(sessionId, message1.Content, limit: 1, threshold: 0.0);
        searchResults.Should().ContainSingle(m => m.Id == message1.Id);

        // ---- POST /list_sessions ----
        var sessions = await ListSessionsAsync();
        sessions.Should().Contain(s => s.SessionId == sessionId);

        // ---- POST /delete_message, then re-run /get_conversation to confirm it's gone ----
        var deleteResponse = await _client.PostAsJsonAsync(
            "/delete_message", new DeleteMessageRequest(message2.Id), WireJson);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var deletedPayload = await deleteResponse.Content.ReadFromJsonAsync<JsonElement>(WireJson);
        deletedPayload.GetProperty("deleted").GetBoolean().Should().BeTrue();

        var afterDelete = await GetConversationAsync(sessionId, limit: null);
        afterDelete.Messages.Select(m => m.Id).Should().Equal(
            new[] { message1.Id, message3.Id },
            "the deleted message must no longer appear, while the surviving ones keep their relative order");

        // ---- POST /clear_session, then confirm /get_conversation returns empty ----
        var clearSessionResponse = await _client.PostAsJsonAsync(
            "/clear_session", new ClearSessionRequest(sessionId), WireJson);
        clearSessionResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterClear = await GetConversationAsync(sessionId, limit: null);
        afterClear.Messages.Should().BeEmpty();

        // ---- POST /clear_all_data (final cleanup) ----
        var clearAllResponse = await _client.PostAsync("/clear_all_data", content: null);
        clearAllResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private async Task<TckMessage> AddMessageAsync(string sessionId, string role, string content)
    {
        var response = await _client.PostAsJsonAsync(
            "/add_message", new AddMessageRequest(sessionId, role, content, null), WireJson);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert directly against the raw wire JSON (not just the deserialized type) that the id and
        // timestamp are server-assigned in the expected shapes, per the task's wire-format requirement.
        var raw = await response.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(raw))
        {
            doc.RootElement.GetProperty("id").GetString().Should().MatchRegex(ServerAssignedId,
                "the bridge — not the client — must assign the message id");
            doc.RootElement.GetProperty("timestamp").GetString().Should().MatchRegex(Iso8601Timestamp,
                "the bridge — not the client — must assign an ISO-8601 timestamp");
        }

        return JsonSerializer.Deserialize<TckMessage>(raw, WireJson)!;
    }

    private static void AssertServerAssigned(TckMessage message)
    {
        message.Id.Should().MatchRegex(ServerAssignedId);
        message.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
    }

    private async Task<TckConversation> GetConversationAsync(string sessionId, int? limit)
    {
        var response = await _client.PostAsJsonAsync(
            "/get_conversation", new GetConversationRequest(sessionId, limit), WireJson);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<TckConversation>(WireJson))!;
    }

    private async Task<IReadOnlyList<TckMessage>> SearchMessagesAsync(
        string sessionId, string query, int limit, double threshold)
    {
        var response = await _client.PostAsJsonAsync(
            "/search_messages", new SearchMessagesRequest(query, sessionId, limit, threshold), WireJson);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<TckMessage>>(WireJson))!;
    }

    private async Task<IReadOnlyList<TckSessionInfo>> ListSessionsAsync()
    {
        var response = await _client.PostAsJsonAsync(
            "/list_sessions", new ListSessionsRequest(Limit: null), WireJson);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<TckSessionInfo>>(WireJson))!;
    }

    /// <summary>
    /// Hosts the bridge's real <c>Program</c> in-process (TestServer — never binds a real port, so it
    /// cannot clash with a real running bridge instance). The <c>ConfigureAppConfiguration</c> override
    /// below is kept as defense-in-depth for any config Program.cs might read lazily (e.g. via DI at
    /// request time rather than into top-level locals), but the value that actually matters — the
    /// Neo4j:*/EmbeddingDimensions settings Program.cs captures into locals before <c>Build()</c> — is
    /// supplied via environment variables scoped around <see cref="CreateClientPointedAtFixtureContainer"/>
    /// instead; see that method's remarks for why.
    /// </summary>
    private sealed class BridgeWebApplicationFactory : WebApplicationFactory<global::Program>
    {
        private readonly Neo4jIntegrationFixture _fixture;

        public BridgeWebApplicationFactory(Neo4jIntegrationFixture fixture) => _fixture = fixture;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Neo4j:Uri"] = _fixture.ConnectionString,
                    ["Neo4j:Username"] = _fixture.User,
                    ["Neo4j:Password"] = _fixture.Password,
                    ["Neo4j:Database"] = "neo4j",
                    ["EmbeddingDimensions"] =
                        Neo4jIntegrationFixture.TestEmbeddingDimensions.ToString(CultureInfo.InvariantCulture),
                });
            });
        }
    }
}
