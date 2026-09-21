using System.Net;
using System.Text;
using AgentMemory.Inference;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.Inference;

/// <summary>
/// The request actually goes where the contract says, carrying what the contract says.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything else in this suite tests the resolver — what the settings say. This tests the
/// last hop: what reaches the wire.</b> Those are different claims, and this repository's signature
/// defect is precisely a value that is resolved, documented and never fed to anything. A settings
/// object holding a 30-second timeout proves nothing about whether the client honours it.
/// </para>
/// <para>
/// It costs nothing: a loopback <see cref="HttpListener"/> stands in for the provider. That is
/// allowed to carry a key because the endpoint policy permits http to loopback exactly so a local
/// server works — the same rule that makes Ollama usable makes this test possible.
/// </para>
/// </remarks>
public sealed class RequestReachesTheConfiguredHostTests
{
    /// <summary>A minimal OpenAI-protocol stand-in that records what it was sent.</summary>
    private sealed class FakeHost : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        internal FakeHost(int delayMilliseconds = 0, string body = """
            {"id":"1","object":"chat.completion","created":1,"model":"m",
             "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}
            """)
        {
            Port = FreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
                    catch { return; }

                    LastPath = context.Request.Url?.AbsolutePath;
                    LastAuthorization = context.Request.Headers["Authorization"];
                    using (var reader = new StreamReader(context.Request.InputStream))
                    {
                        LastBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                    }

                    if (delayMilliseconds > 0)
                    {
                        try { await Task.Delay(delayMilliseconds, _cts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }

                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    try
                    {
                        await context.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                        context.Response.Close();
                    }
                    catch { /* the client gave up first; that is what some of these tests assert */ }
                }
            });
        }

        internal int Port { get; }

        internal string? LastPath { get; private set; }

        internal string? LastBody { get; private set; }

        internal string? LastAuthorization { get; private set; }

        internal string Endpoint => $"http://127.0.0.1:{Port}/v1";

        private static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _listener.Abort();
            try { await _loop.ConfigureAwait(false); } catch { /* shutting down */ }
            _cts.Dispose();
        }
    }

    private static InferenceProviderSettings Settings(string endpoint, string model = "the-configured-model") => new()
    {
        Provider = InferenceProvider.OpenAICompatible,
        Endpoint = endpoint,
        ApiKey = "sk-test-key-value",
        Model = model,
        EmbeddingProvider = InferenceProvider.OpenAICompatible,
        EmbeddingEndpoint = endpoint,
        EmbeddingApiKey = "sk-test-key-value",
        EmbeddingModel = "the-embedding-model",
        EmbeddingDimensions = 8,
    };

    /// <summary>The chat call reaches the configured endpoint with the configured model and key.</summary>
    [Fact]
    public async Task TheChatRequestCarriesTheConfiguredEndpointModelAndKey()
    {
        await using var host = new FakeHost();

        InferenceClientFactory
            .TryCreateChatClient(Settings(host.Endpoint), "answer", out var client, out var diagnostic)
            .Should().BeTrue(diagnostic);

        await client!.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        host.LastPath.Should().Be("/v1/chat/completions", "the base URL must be honoured, not replaced");
        host.LastBody.Should().Contain("the-configured-model", "the resolved model must reach the wire");
        host.LastAuthorization.Should().Be(
            "Bearer sk-test-key-value", "the resolved key must reach the host that was configured");
    }

    /// <summary>
    /// A role's model override reaches the wire, rather than the primary.
    /// </summary>
    /// <remarks>
    /// <c>AGENTMEMORY_EXTRACTION_MODEL</c> exists to run extraction on a different model. Resolving it
    /// into settings and then sending the primary anyway is the defect this whole phase is about.
    /// </remarks>
    [Fact]
    public async Task ARoleModelOverrideReachesTheWire()
    {
        await using var host = new FakeHost();

        InferenceClientFactory.TryCreateChatClient(
                Settings(host.Endpoint), "extraction", out var client, out var diagnostic,
                model: "the-extraction-model")
            .Should().BeTrue(diagnostic);

        await client!.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        host.LastBody.Should().Contain("the-extraction-model");
        host.LastBody.Should().NotContain("the-configured-model", "the override must REPLACE the primary");
    }

    /// <summary>
    /// The configured network timeout actually bounds the call.
    /// </summary>
    /// <remarks>
    /// The last hop nothing else can see: the value travels environment → settings → client options,
    /// and only the client's own behaviour proves the final step. The host here delays well past the
    /// configured second, so a client that ignored the setting would return normally.
    /// </remarks>
    [Fact]
    public async Task TheConfiguredTimeoutBoundsTheCall()
    {
        await using var host = new FakeHost(delayMilliseconds: 10_000);

        var settings = Settings(host.Endpoint) with { NetworkTimeoutSeconds = 1 };
        InferenceClientFactory
            .TryCreateChatClient(settings, "answer", out var client, out var diagnostic)
            .Should().BeTrue(diagnostic);

        var started = DateTimeOffset.UtcNow;
        var act = async () => await client!.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        await act.Should().ThrowAsync<Exception>("a one-second timeout must fire against a ten-second host");
        (DateTimeOffset.UtcNow - started).Should().BeLessThan(
            TimeSpan.FromSeconds(8), "it must give up on ITS schedule, not the host's");
    }

    /// <summary>
    /// Embeddings go to their own host when the override block points them elsewhere.
    /// </summary>
    /// <remarks>
    /// The "chat on Bitdeer, embeddings on a local Ollama" case. Two hosts, and the test fails if
    /// either request lands on the wrong one — which a settings-only assertion cannot detect.
    /// </remarks>
    [Fact]
    public async Task EmbeddingsGoToTheirOwnHostWhenOverridden()
    {
        await using var chatHost = new FakeHost();
        await using var embeddingHost = new FakeHost(body: """
            {"object":"list","model":"e","data":[{"object":"embedding","index":0,
             "embedding":[0.1,0.2,0.3,0.4,0.5,0.6,0.7,0.8]}]}
            """);

        var settings = Settings(chatHost.Endpoint) with
        {
            EmbeddingEndpoint = embeddingHost.Endpoint,
            EmbeddingApiKey = "sk-embedding-key",
        };

        InferenceClientFactory
            .TryCreateEmbeddingGenerator(settings, out var generator, out var diagnostic)
            .Should().BeTrue(diagnostic);

        await generator!.GenerateAsync(["text"]);

        embeddingHost.LastPath.Should().Be("/v1/embeddings", "embeddings went to the embedding host");
        embeddingHost.LastAuthorization.Should().Be(
            "Bearer sk-embedding-key", "and carried the embedding host's own key");
        chatHost.LastPath.Should().BeNull("the chat host must not have been touched at all");
    }
}
