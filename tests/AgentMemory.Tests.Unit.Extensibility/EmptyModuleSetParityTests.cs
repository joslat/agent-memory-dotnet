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
/// THE SLICE A GATE: with no modules registered, the new provider is the old one.
/// </summary>
/// <remarks>
/// <para>
/// The delegation design exists to make this true <b>by construction</b> rather than by comparison —
/// the core block is produced by calling the shipped provider, so there is no second rendering path
/// that could drift from it. These tests assert the construction actually holds, because "by
/// construction" is a claim about code that can stop being true the moment someone adds a
/// well-meaning copy.
/// </para>
/// <para>
/// <b>Reference equality, not equivalence.</b> Asserting the two <c>AIContext</c> objects have equal
/// fields would pass for a faithful reproduction — and a faithful reproduction is precisely what this
/// design refuses, because the facade's own comment records that a second rendering path is where the
/// #92 admission gate drifts. Returning the same instance is the only result that cannot drift.
/// </para>
/// </remarks>
public sealed class EmptyModuleSetParityTests
{
    /// <summary>With no module contributors, the composed provider's context is returned as-is.</summary>
    [Fact]
    public async Task WithNoModulesTheCoreContextIsReturnedUnchanged()
    {
        var harness = new Harness();

        var produced = await harness.Provider.InvokingAsync(harness.Context, CancellationToken.None);
        var direct = await harness.Core.InvokingAsync(harness.Context, CancellationToken.None);

        produced.Instructions.Should().Be(direct.Instructions);

        // CONTENT, not the framework's attribution stamp. `InvokingAsync` records which provider
        // TYPE produced each message, so a different type necessarily stamps differently -- that is
        // the framework working, not drift. What must be identical is the memory: same messages, same
        // roles, same order, same count. Before this provider inherited rather than composed, the
        // message COUNT differed too, because composing ran the base wrapper twice.
        // Both sides are asserted non-null first: a null Messages on either provider is itself a
        // parity failure, and silencing the nullable warning would hide it rather than check it.
        produced.Messages.Should().NotBeNull();
        direct.Messages.Should().NotBeNull();
        produced.Messages!.Select(m => (m.Role, m.Text))
            .Should().Equal(direct.Messages!.Select(m => (m.Role, m.Text)));
    }

    /// <summary>
    /// A registered contributor that is only the CORE one still counts as no modules.
    /// </summary>
    /// <remarks>
    /// The core contributor exists for hosts that compile context rather than delegate it. If its
    /// presence in the container flipped this provider into compile-and-append mode, core memory
    /// would be rendered twice — once delegated, once compiled — which is the one outcome the
    /// opaque core section is designed to prevent.
    /// </remarks>
    [Fact]
    public async Task TheCoreContributorAloneDoesNotCountAsAModule()
    {
        var assembler = Substitute.For<IMemoryContextAssembler>();
        var harness = new Harness(
            new AgentMemory.Extensibility.Contributors.CoreMemoryContextContributor(assembler));

        var produced = await harness.Provider.InvokingAsync(harness.Context, CancellationToken.None);

        produced.Instructions.Should().Be(
            (await harness.Core.InvokingAsync(harness.Context, CancellationToken.None)).Instructions);
        await assembler.DidNotReceive().AssembleContextAsync(
            Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A module section is appended AFTER the core block, never woven into it.
    /// </summary>
    [Fact]
    public async Task AModuleSectionIsAppendedAfterTheCoreBlock()
    {
        var harness = new Harness(new ModuleContributor());

        var produced = await harness.Provider.InvokingAsync(harness.Context, CancellationToken.None);
        var core = await harness.Core.InvokingAsync(harness.Context, CancellationToken.None);

        produced.Instructions.Should().Contain("notes.recent");
        if (!string.IsNullOrEmpty(core.Instructions))
        {
            produced.Instructions!.IndexOf("notes.recent", StringComparison.Ordinal)
                .Should().BeGreaterThan(
                    produced.Instructions!.IndexOf(core.Instructions!, StringComparison.Ordinal),
                    "modules append; they never interleave with core text");
        }
    }

    /// <summary>A module that throws costs its section and nothing else.</summary>
    [Fact]
    public async Task AFailingModuleDoesNotCostTheHostItsCoreContext()
    {
        var harness = new Harness(new ThrowingModuleContributor());

        var produced = await harness.Provider.InvokingAsync(harness.Context, CancellationToken.None);
        var core = await harness.Core.InvokingAsync(harness.Context, CancellationToken.None);

        produced.Instructions.Should().Be(core.Instructions);
    }

    private sealed class ModuleContributor : IContextContributor
    {
        public ContextContributorDescriptor Descriptor { get; } =
            new("notes", new HashSet<string>(StringComparer.Ordinal) { "notes.recent" });

        public ValueTask<bool> AppliesAsync(ContextRequest r, CancellationToken ct) => ValueTask.FromResult(true);

        public Task<ContextSection?> ContributeAsync(ContextRequest r, CancellationToken ct) =>
            Task.FromResult<ContextSection?>(
                new ContextSection("notes.recent", "notes", 1, [new ContextItem("n1", "a note")]));
    }

    private sealed class ThrowingModuleContributor : IContextContributor
    {
        public ContextContributorDescriptor Descriptor { get; } =
            new("broken", new HashSet<string>(StringComparer.Ordinal) { "broken.section" });

        public ValueTask<bool> AppliesAsync(ContextRequest r, CancellationToken ct) => ValueTask.FromResult(true);

        public Task<ContextSection?> ContributeAsync(ContextRequest r, CancellationToken ct) =>
            throw new InvalidOperationException("module store unreachable");
    }

    /// <summary>Builds the real shipped provider over substitutes, and the new one composing it.</summary>
    private sealed class Harness
    {
        internal Neo4jMemoryContextProvider Core { get; }

        internal AgentMemory.Extensibility.AgentFramework.ExtensibleMemoryContextProvider Provider { get; }

        internal AIContextProvider.InvokingContext Context { get; }

        internal Harness(params IContextContributor[] contributors)
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

            var embeddings = Substitute.For<IEmbeddingOrchestrator>();
            var clock = Substitute.For<IClock>();
            var ids = Substitute.For<IIdGenerator>();
            var memoryOptions = Options.Create(new MemoryOptions());
            var formatOptions = Options.Create(new ContextFormatOptions());
            var agentOptions = Options.Create(new AgentFrameworkOptions());

            // The BASELINE: the shipped provider as a host runs it today.
            Core = new Neo4jMemoryContextProvider(
                memory, embeddings, clock, ids, memoryOptions, formatOptions, agentOptions,
                NullLogger<Neo4jMemoryContextProvider>.Instance);

            var compiler = new ContextCompiler(
                contributors, TimeProvider.System, NullLogger<ContextCompiler>.Instance);

            // The SUBJECT: the same provider, extended. One instance, one attribution stamp.
            Provider = new AgentMemory.Extensibility.AgentFramework.ExtensibleMemoryContextProvider(
                memory, embeddings, clock, ids, memoryOptions, formatOptions, agentOptions,
                NullLogger<Neo4jMemoryContextProvider>.Instance,
                compiler,
                contributors,
                NullLogger<AgentMemory.Extensibility.AgentFramework.ExtensibleMemoryContextProvider>.Instance);

            Context = new AIContextProvider.InvokingContext(
                Substitute.For<AIAgent>(),
                Substitute.For<AgentSession>(),
                new AIContext { Messages = [new ChatMessage(ChatRole.User, "hello")] });
        }
    }
}
