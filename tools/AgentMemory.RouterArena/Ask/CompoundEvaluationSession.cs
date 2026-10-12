using System.ClientModel;
using System.ClientModel.Primitives;
using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace AgentMemory.RouterArena.Ask;

/// <summary>A fresh case owns its transports, so late work cannot be attributed to the following case.</summary>
internal sealed class CompoundEvaluationSession : IDisposable
{
    private readonly CompoundHttpMeter _gateMeter;
    private readonly CompoundHttpMeter _chatMeter;
    private readonly HttpClient _gateHttp;
    private readonly HttpClient _chatHttp;
    private readonly IChatClient _chat;
    private readonly JudgeFailureCounter _judgeFailures = new();
    public IMemoryGate Gate { get; }
    public ICompoundQuestionPlanner Planner { get; }

    public CompoundEvaluationSession(MemoryGateOptions options, string endpoint, string key, string model,
        HttpMessageHandler? gateTransport = null, HttpMessageHandler? chatTransport = null)
    {
        _gateMeter = new CompoundHttpMeter(gateTransport ?? new SocketsHttpHandler { AllowAutoRedirect = false });
        _chatMeter = new CompoundHttpMeter(chatTransport ?? new SocketsHttpHandler { AllowAutoRedirect = false });
        _gateHttp = new HttpClient(_gateMeter) { Timeout = Timeout.InfiniteTimeSpan };
        _chatHttp = new HttpClient(_chatMeter) { Timeout = Timeout.InfiniteTimeSpan };
        // The experiment pins SDK retries to zero; System One retains its library retry policy.
        // Count beneath both protocols, rather than treating a logical chat/gate call as a physical request.
        _chat = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint), Transport = new HttpClientPipelineTransport(_chatHttp),
            RetryPolicy = new ClientRetryPolicy(0), NetworkTimeout = options.Timeout,
        }).GetChatClient(model).AsIChatClient();
        var settings = Options.Create(options);
        var client = new SystemOneClient(_gateHttp);
        Gate = new SystemOneMemoryGate(client, settings, _judgeFailures);
        Planner = new SystemOneCompoundQuestionPlanner(client, _chat, settings);
    }

    public CompoundRequestCounts Snapshot() => CompoundHttpMeter.Sum(_gateMeter.Snapshot(), _chatMeter.Snapshot()) with
    {
        JudgeFailures = _judgeFailures.Count,
    };

    public void Dispose()
    {
        _chat.Dispose();
        _chatHttp.Dispose();
        _gateHttp.Dispose();
    }

    private sealed class JudgeFailureCounter : ILogger<SystemOneMemoryGate>
    {
        private long _count;
        public long Count => Interlocked.Read(ref _count);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Do not invoke the formatter: only count failures, never persist private provider messages.
            if (IsEnabled(logLevel)) Interlocked.Increment(ref _count);
        }
    }
}
