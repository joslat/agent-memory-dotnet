using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Module contributors run inside the tenant's store scope, not beside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Holding the dependency is not the same as being inside the scope.</b> The constructor threads
/// <see cref="IMemoryStoreContext"/> through to the base — which was itself a fix — but the base
/// opens its store scope with <c>using</c> for the duration of its own method. Module compilation
/// happens after <c>base.ProvideAIContextAsync</c> returns, so it ran with the scope already
/// disposed: a contributor resolving the ambient store context saw the DEFAULT store while the core
/// block beside it came from the tenant's.
/// </para>
/// <para>
/// Nothing throws when this is wrong. A multi-tenant host is simply served the wrong store, which is
/// the failure mode least likely to be noticed and most expensive to have shipped.
/// </para>
/// </remarks>
public sealed class ModuleTenantScopeTests
{
    private const string Tenant = "tenant-a";

    /// <summary>The contributor observes the tenant's application id while it runs.</summary>
    [Fact]
    public async Task AModuleRunsInsideTheTenantStoreScope()
    {
        var store = new RecordingStoreContext();
        var contributor = new StoreObservingContributor(store);
        var harness = new Harness(store, contributor);

        await harness.Provider.InvokingAsync(harness.Context, CancellationToken.None);

        contributor.ObservedApplicationId.Should().Be(
            Tenant,
            "a module querying the ambient store context must land on the same store the core block did");
    }

    /// <summary>And the scope is released afterwards, rather than leaking past the turn.</summary>
    /// <remarks>
    /// The mirror of the bug: re-opening a scope is only correct if it is also closed. A scope left
    /// open would pin one tenant's store onto whatever ran next on this context.
    /// </remarks>
    [Fact]
    public async Task TheScopeIsReleasedWhenTheTurnEnds()
    {
        var store = new RecordingStoreContext();
        var harness = new Harness(store, new StoreObservingContributor(store));

        await harness.Provider.InvokingAsync(harness.Context, CancellationToken.None);

        store.ApplicationId.Should().BeNull("the scope must not outlive the turn that opened it");
    }

    private sealed class RecordingStoreContext : IWritableMemoryStoreContext
    {
        public string? ApplicationId { get; set; }
    }

    private sealed class StoreObservingContributor(IMemoryStoreContext store) : IContextContributor
    {
        internal string? ObservedApplicationId { get; private set; }

        public ContextContributorDescriptor Descriptor { get; } =
            new("observer", new HashSet<string>(StringComparer.Ordinal) { "observer.section" });

        public ValueTask<bool> AppliesAsync(ContextRequest r, CancellationToken ct) => ValueTask.FromResult(true);

        public Task<ContextSection?> ContributeAsync(ContextRequest r, CancellationToken ct)
        {
            // Exactly what a module doing its own retrieval would read.
            ObservedApplicationId = store.ApplicationId;
            return Task.FromResult<ContextSection?>(new ContextSection(
                "observer.section", "observer", 1, [new ContextItem("o1", "observed")]));
        }
    }

    /// <summary>AgentSession is abstract; a minimal subclass is all the state bag needs.</summary>
    private sealed class TestAgentSession : AgentSession;

    private sealed class Harness
    {
        internal AgentMemory.Extensibility.AgentFramework.ExtensibleMemoryContextProvider Provider { get; }

        internal AIContextProvider.InvokingContext Context { get; }

        internal Harness(IWritableMemoryStoreContext store, params IContextContributor[] contributors)
        {
            var memory = Substitute.For<IMemoryService>();
            memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
                .Returns(new RecallResult
                {
                    Context = new MemoryContext
                    {
                        SessionId = "s-1",
                        AssembledAtUtc = DateTimeOffset.UnixEpoch,
                    },
                });

            var isolation = Substitute.For<IMemoryIsolationPolicy>();
            isolation.ResolveReadScope(
                    Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(),
                    Arg.Any<MemoryOperationAccess>())
                .Returns(call => MemoryScope.For(call.ArgAt<string?>(1) ?? "anonymous"));

            var compiler = new ContextCompiler(
                contributors, isolation, TimeProvider.System, NullLogger<ContextCompiler>.Instance);

            var agentOptions = new AgentFrameworkOptions();

            Provider = new AgentMemory.Extensibility.AgentFramework.ExtensibleMemoryContextProvider(
                memory,
                Substitute.For<IEmbeddingOrchestrator>(),
                Substitute.For<IClock>(),
                Substitute.For<IIdGenerator>(),
                Options.Create(new MemoryOptions()),
                Options.Create(new ContextFormatOptions()),
                Options.Create(agentOptions),
                NullLogger<Neo4jMemoryContextProvider>.Instance,
                compiler,
                contributors,
                NullLogger<AgentMemory.Extensibility.AgentFramework.ExtensibleMemoryContextProvider>.Instance,
                storeContext: store);

            var session = new TestAgentSession();
            session.StateBag.SetValue(
                agentOptions.DefaultApplicationIdKey, Tenant,
                System.Text.Json.JsonSerializerOptions.Default);
            session.StateBag.SetValue(
                agentOptions.DefaultSessionIdKey, "s-1",
                System.Text.Json.JsonSerializerOptions.Default);

            Context = new AIContextProvider.InvokingContext(
                Substitute.For<AIAgent>(),
                session,
                new AIContext { Messages = [new ChatMessage(ChatRole.User, "hello")] });
        }
    }
}
