using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Core.Services;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// Proves the actual cross-await AsyncLocal propagation fix for #90 -- not just wiring. Uses a REAL
/// (non-substitute) <see cref="DefaultMemoryOwnerContext"/> and an inner agent whose <c>RunCoreAsync</c>
/// genuinely suspends (<c>await Task.Yield()</c>) before invoking a callback that simulates a
/// model-invoked tool call reading the ambient owner. Today's <c>Neo4jMemoryContextProviderTests</c> only
/// assert a substitute received a property-set call, which does not prove the value survives a real
/// suspend/resume boundary the way this test does.
/// </summary>
public sealed class MemoryOwnerScopingAgentTests
{
    // Simulates a MAF inner agent whose invocation genuinely suspends (real async I/O, e.g. recall) before
    // the "tool calling loop" -- represented here by invoking a caller-supplied callback -- runs.
    private sealed class FakeInnerAgent : AIAgent
    {
        private readonly Func<CancellationToken, Task> _simulatedToolCall;

        public FakeInnerAgent(Func<CancellationToken, Task> simulatedToolCall) => _simulatedToolCall = simulatedToolCall;

        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // A genuine suspend point -- mirrors ProvideAIContextAsync awaiting real recall I/O.
            await Task.Yield();
            await _simulatedToolCall(cancellationToken);
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            await _simulatedToolCall(cancellationToken);
            yield return new AgentResponseUpdate(ChatRole.Assistant, "ok");
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentSession>(new TestAgentSession());

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedSession, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentSession>(new TestAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(default(JsonElement));
    }

    private static AgentSession SessionFor(string? userId) =>
        new TestAgentSession().WithMemoryIdentity(userId: userId, sessionId: "s1", conversationId: "c1");

    private sealed class TestAgentSession : AgentSession;

    [Fact]
    public async Task RunAsync_SimulatedToolCallAfterGenuineSuspend_ObservesConfiguredOwner()
    {
        var ownerContext = new DefaultMemoryOwnerContext();
        string? observedByTool = "not-set";

        var inner = new FakeInnerAgent(_ =>
        {
            observedByTool = ownerContext.UserId;
            return Task.CompletedTask;
        });
        var scopedAgent = inner.WithMemoryOwnerScoping(ownerContext);

        await scopedAgent.RunAsync("hello", SessionFor("alice"));

        observedByTool.Should().Be("alice",
            "the owner scope must still be visible to a tool call that runs after a genuine await, " +
            "which a bare (non-scope-wrapping) assignment inside a context-provider hook cannot guarantee");
    }

    [Fact]
    public async Task RunAsync_AfterInvocation_RestoresPreviousAmbientOwner()
    {
        var ownerContext = new DefaultMemoryOwnerContext { UserId = "outer" };
        var inner = new FakeInnerAgent(_ => Task.CompletedTask);
        var scopedAgent = inner.WithMemoryOwnerScoping(ownerContext);

        await scopedAgent.RunAsync("hello", SessionFor("alice"));

        ownerContext.UserId.Should().Be("outer", "the scope must not leak past the invocation it wrapped");
    }

    [Fact]
    public async Task RunStreamingAsync_SimulatedToolCallAfterGenuineSuspend_ObservesConfiguredOwner()
    {
        var ownerContext = new DefaultMemoryOwnerContext();
        string? observedByTool = "not-set";

        var inner = new FakeInnerAgent(_ =>
        {
            observedByTool = ownerContext.UserId;
            return Task.CompletedTask;
        });
        var scopedAgent = inner.WithMemoryOwnerScoping(ownerContext);

        await foreach (var _ in scopedAgent.RunStreamingAsync("hello", SessionFor("bob"))) { }

        observedByTool.Should().Be("bob");
    }

    [Fact]
    public async Task ParallelInvocations_ForDifferentOwners_DoNotLeakOwnerScope()
    {
        // The issue's explicit acceptance criterion: concurrent invocations for different owners must not
        // leak into one another, even though both share the same singleton-style DefaultMemoryOwnerContext
        // instance (matching how it's registered in DI -- a singleton, relying on AsyncLocal for
        // per-flow isolation, not per-DI-scope isolation).
        var ownerContext = new DefaultMemoryOwnerContext();
        string? aliceObserved = null;
        string? bobObserved = null;

        var aliceAgent = new FakeInnerAgent(async _ =>
        {
            await Task.Delay(20); // interleave with the other concurrent invocation
            aliceObserved = ownerContext.UserId;
        }).WithMemoryOwnerScoping(ownerContext);

        var bobAgent = new FakeInnerAgent(async _ =>
        {
            await Task.Delay(5);
            bobObserved = ownerContext.UserId;
        }).WithMemoryOwnerScoping(ownerContext);

        await Task.WhenAll(
            aliceAgent.RunAsync("hi", SessionFor("alice")),
            bobAgent.RunAsync("hi", SessionFor("bob")));

        aliceObserved.Should().Be("alice");
        bobObserved.Should().Be("bob");
    }

    [Fact]
    public async Task RunAsync_NoUserIdOnSession_ScopesToSharedGlobal()
    {
        var ownerContext = new DefaultMemoryOwnerContext { UserId = "stale-from-previous-turn" };
        string? observed = "not-set";
        var inner = new FakeInnerAgent(_ =>
        {
            observed = ownerContext.UserId;
            return Task.CompletedTask;
        });
        var scopedAgent = inner.WithMemoryOwnerScoping(ownerContext);

        await scopedAgent.RunAsync("hello", SessionFor(userId: null));

        observed.Should().BeNull("an unscoped session must not inherit a stale owner left over from a previous turn");
    }

    [Fact]
    public async Task WithMemoryOwnerScoping_ServiceProviderOverload_ResolvesCustomizedKeyNamesConsistently()
    {
        // Regression test for a real gap an adversarial review caught: if a host customizes
        // AgentFrameworkOptions.Default*Key, the wrapper and the provider MUST read the session's identity
        // under the same key -- otherwise the wrapper silently scopes the whole invocation (incl. tool
        // calls) to no owner, even though the identity WAS written to the session correctly. The
        // IServiceProvider overload resolves the same registered AgentFrameworkOptions the provider does,
        // so this can't drift out of sync.
        var services = new ServiceCollection();
        services.AddOptions<AgentFrameworkOptions>().Configure(o => o.DefaultUserIdKey = "custom_user_key");
        services.AddSingleton<DefaultMemoryOwnerContext>();
        services.AddSingleton<IWritableMemoryOwnerContext>(sp => sp.GetRequiredService<DefaultMemoryOwnerContext>());
        var provider = services.BuildServiceProvider();

        var agentOptions = provider.GetRequiredService<IOptions<AgentFrameworkOptions>>().Value;
        var session = new TestAgentSession().WithMemoryIdentity(userId: "alice", sessionId: "s1", options: agentOptions);

        string? observed = "not-set";
        var inner = new FakeInnerAgent(_ =>
        {
            observed = provider.GetRequiredService<IWritableMemoryOwnerContext>().UserId;
            return Task.CompletedTask;
        });

        var scopedAgent = inner.WithMemoryOwnerScoping(provider);

        await scopedAgent.RunAsync("hello", session);

        observed.Should().Be("alice", "the IServiceProvider overload must read the same customized StateBag key the identity was written under");
    }

    [Fact]
    public void WithMemoryOwnerScoping_ManualOverloadWithMismatchedOptions_DocumentsTheFootgunItReplaces()
    {
        // Documents the exact failure mode the IServiceProvider overload above exists to prevent: the
        // manual (ownerContext-only) overload defaults to un-customized AgentFrameworkOptions when no
        // options are passed, so if the session's identity was written under a CUSTOMIZED key, the
        // wrapper reads the WRONG (default) key and gets nothing -- silently unscoping the invocation.
        var writerOptions = new AgentFrameworkOptions { DefaultUserIdKey = "custom_user_key" };
        var session = new TestAgentSession().WithMemoryIdentity(userId: "alice", sessionId: "s1", options: writerOptions);

        // The wrapper, using default (un-customized) options because none were passed:
        var identityAsWrapperSeesIt = session.GetMemoryIdentity(options: null);

        identityAsWrapperSeesIt.UserId.Should().BeNull(
            "this is why the IServiceProvider overload (which resolves the SAME registered options) is recommended over passing an IWritableMemoryOwnerContext directly");
    }
}
