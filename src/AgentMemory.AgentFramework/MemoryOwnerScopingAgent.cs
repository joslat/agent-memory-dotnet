using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.AgentFramework;

/// <summary>
/// A <see cref="DelegatingAIAgent"/> decorator that guarantees the memory owner (and, optionally,
/// application/store) scope encloses a <em>complete</em> agent invocation — passive recall (any
/// <c>AIContextProvider</c>/<c>ChatHistoryProvider</c> hooks), the model call, the full tool-calling loop
/// (so <c>search_memory</c>/<c>remember_*</c> etc. observe the same owner), and automatic persistence —
/// as one unbroken async call chain (#90).
/// </summary>
/// <remarks>
/// This exists because <c>AIContextProvider</c> cannot do this on its own: its
/// <c>ProvideAIContextAsync</c>/<c>StoreAIContextAsync</c> hooks are a pre/post pair around the
/// invocation, not a bracket around it. Setting <see cref="IWritableMemoryOwnerContext.UserId"/> inside
/// <c>ProvideAIContextAsync</c> does not survive into the tool-calling loop, because that hook genuinely
/// suspends on real I/O (embedding + recall) — once MAF's own <c>await</c> on the hook resumes, the
/// <see cref="System.Threading.AsyncLocal{T}"/>-backed value set inside it is gone. Wrapping the entire
/// invocation here, with no intervening top-level <c>await</c>, keeps the <c>AsyncLocal</c> mutation
/// visible for the whole call chain.
/// </remarks>
public sealed class MemoryOwnerScopingAgent : DelegatingAIAgent
{
    private readonly IWritableMemoryOwnerContext _ownerContext;
    private readonly IWritableMemoryStoreContext? _storeContext;
    private readonly AgentFrameworkOptions? _options;
    private readonly ILogger _logger;

    public MemoryOwnerScopingAgent(
        AIAgent innerAgent,
        IWritableMemoryOwnerContext ownerContext,
        IWritableMemoryStoreContext? storeContext = null,
        AgentFrameworkOptions? options = null,
        ILogger<MemoryOwnerScopingAgent>? logger = null)
        : base(innerAgent)
    {
        _ownerContext = ownerContext ?? throw new ArgumentNullException(nameof(ownerContext));
        _storeContext = storeContext;
        _options = options;
        _logger = logger ?? NullLogger<MemoryOwnerScopingAgent>.Instance;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var identity = GetIdentity(session);
        using var ownerScope = _ownerContext.BeginOwnerScope(identity.UserId);
        using var storeScope = _storeContext?.BeginStoreScope(identity.ApplicationId);

        return await base.RunCoreAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var identity = GetIdentity(session);
        using var ownerScope = _ownerContext.BeginOwnerScope(identity.UserId);
        using var storeScope = _storeContext?.BeginStoreScope(identity.ApplicationId);

        await foreach (var update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    // Matches the providers' own tolerance for a malformed StateBag value (e.g. a rehydrated/persisted
    // session whose bag holds a non-string JSON value under an identity key) -- fail soft to an unscoped
    // identity rather than crash the whole invocation.
    private MemoryIdentity GetIdentity(AgentSession? session)
    {
        try
        {
            return session.GetMemoryIdentity(_options);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract memory identity from session state bag.");
            return default;
        }
    }
}

/// <summary>
/// Extension for applying <see cref="MemoryOwnerScopingAgent"/> to an existing <see cref="AIAgent"/>.
/// </summary>
public static class AIAgentMemoryScopingExtensions
{
    /// <summary>
    /// Wraps <paramref name="agent"/> so every invocation — passive recall, the model call, the full
    /// tool-calling loop, and automatic persistence — runs inside the same owner (and, if
    /// <paramref name="storeContext"/> is supplied, application/store) scope, guaranteed for the complete
    /// invocation rather than only the portion inside a context-provider hook (#90). Apply this once at
    /// agent-construction time instead of manually wrapping every <c>RunAsync</c> call in
    /// <c>ownerContext.BeginOwnerScope(userId)</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="options"/> must be the SAME <see cref="AgentFrameworkOptions"/> instance (or an
    /// equivalent one, if the host customized <c>Default*Key</c> names) that <see cref="Neo4jMemoryContextProvider"/>
    /// / <see cref="Neo4jChatHistoryProvider"/> were configured with — otherwise this wrapper reads the
    /// session's identity under the wrong StateBag keys, finds nothing, and silently scopes the whole
    /// invocation (including tool calls) to no owner. Prefer the
    /// <see cref="WithMemoryOwnerScoping(AIAgent, IServiceProvider)"/> overload, which resolves the exact
    /// same registered options the provider uses and cannot drift out of sync.
    /// </remarks>
    public static AIAgent WithMemoryOwnerScoping(
        this AIAgent agent,
        IWritableMemoryOwnerContext ownerContext,
        IWritableMemoryStoreContext? storeContext = null,
        AgentFrameworkOptions? options = null,
        ILogger<MemoryOwnerScopingAgent>? logger = null) =>
        new MemoryOwnerScopingAgent(agent, ownerContext, storeContext, options, logger);

    /// <summary>
    /// Wraps <paramref name="agent"/> exactly like
    /// <see cref="WithMemoryOwnerScoping(AIAgent, IWritableMemoryOwnerContext, IWritableMemoryStoreContext?, AgentFrameworkOptions?, ILogger{MemoryOwnerScopingAgent}?)"/>,
    /// but resolves <see cref="IWritableMemoryOwnerContext"/>, the registered <see cref="AgentFrameworkOptions"/>,
    /// and (if registered) <see cref="IWritableMemoryStoreContext"/> from <paramref name="serviceProvider"/> --
    /// the same container <c>Neo4jMemoryContextProvider</c>/<c>Neo4jChatHistoryProvider</c> resolve theirs
    /// from, so the StateBag key names used to read the session's identity can never drift out of sync
    /// with the ones the provider was configured with. This is the recommended overload.
    /// </summary>
    public static AIAgent WithMemoryOwnerScoping(this AIAgent agent, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        var ownerContext = serviceProvider.GetRequiredService<IWritableMemoryOwnerContext>();
        var storeContext = serviceProvider.GetService<IWritableMemoryStoreContext>();
        var options = serviceProvider.GetService<IOptions<AgentFrameworkOptions>>()?.Value;
        var logger = serviceProvider.GetService<ILogger<MemoryOwnerScopingAgent>>();
        return new MemoryOwnerScopingAgent(agent, ownerContext, storeContext, options, logger);
    }
}
