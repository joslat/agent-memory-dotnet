using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Extensibility.AgentFramework;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Capabilities;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleReadTimeAdapterTests
{
    [Fact]
    public async Task NativeInvocationPassesBothSelectedTimesAndIdentityToModuleCompiler()
    {
        var harness = new Harness();
        var system = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.FromHours(2));
        var valid = system.AddDays(-2);
        harness.Session.WithModuleReadTime(system, valid).Should().BeSameAs(harness.Session);

        await harness.InvokeAsync();

        harness.Observer.Request.Should().NotBeNull();
        var request = harness.Observer.Request!;
        request.SystemAsOf.Should().Be(system.ToUniversalTime());
        request.SystemAsOf!.Value.Offset.Should().Be(TimeSpan.Zero);
        request.AsOf.Should().Be(valid.ToUniversalTime());
        request.Owner.Should().Be("alex");
        request.SessionId.Should().Be("fresh-session");
        request.Query.Should().Be("What was due then?");
    }

    [Fact]
    public async Task ClearingSelectionsRestoresCurrentReadsOnTheSameSession()
    {
        var harness = new Harness();
        harness.Session.WithModuleReadTime(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        await harness.InvokeAsync();
        harness.Observer.Request!.SystemAsOf.Should().Be(DateTimeOffset.UnixEpoch);

        harness.Session.WithModuleReadTime();
        await harness.InvokeAsync();

        harness.Observer.Request!.SystemAsOf.Should().BeNull();
        harness.Observer.Request.AsOf.Should().BeNull();
    }

    [Theory]
    [InlineData("agentmemory.modules.system_as_of", "\"not-a-time\"")]
    [InlineData("agentmemory.modules.valid_as_of", "\"\"")]
    [InlineData("agentmemory.modules.system_as_of", "{}")]
    [InlineData("agentmemory.modules.valid_as_of", "42")]
    public async Task MalformedPersistedSelectionNeverRunsModuleAtTheCurrentTime(string key, string json)
    {
        var harness = new Harness();
        harness.Session.StateBag.SetValue<object>(key, JsonSerializer.Deserialize<JsonElement>(json), JsonSerializerOptions.Default);

        await harness.InvokeAsync();

        harness.Observer.Request.Should().BeNull("invalid time selection must not become an unpinned read");
        await harness.Memory.Received(1).RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutSelectionsModuleReadsRemainCurrent()
    {
        var harness = new Harness();
        await harness.InvokeAsync();
        harness.Observer.Request.Should().NotBeNull();
        harness.Observer.Request!.SystemAsOf.Should().BeNull();
        harness.Observer.Request.AsOf.Should().BeNull();
    }

    [Fact]
    public async Task NoModulesIgnoresEvenMalformedTimeState()
    {
        var harness = new Harness(withModules: false);
        harness.Session.StateBag.SetValue(AgentSessionModuleExtensions.SystemTimeKey,
            new { invalid = true }, JsonSerializerOptions.Default);
        await harness.InvokeAsync();
        harness.Observer.Request.Should().BeNull();
        await harness.Memory.Received(1).RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void SelectedTimesSurviveStateBagSerializationAndClearing()
    {
        var original = new TestSession();
        var time = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.FromHours(2));
        original.WithModuleReadTime(time, time.AddDays(-1));
        var restored = new TestSession(AgentSessionStateBag.Deserialize(original.StateBag.Serialize()));
        var selected = AgentSessionModuleExtensions.ReadModuleTimes(restored);
        selected.SystemAsOf.Should().Be(time.ToUniversalTime());
        selected.AsOf.Should().Be(time.AddDays(-1).ToUniversalTime());
        restored.WithModuleReadTime();
        var cleared = new TestSession(AgentSessionStateBag.Deserialize(restored.StateBag.Serialize()));
        AgentSessionModuleExtensions.ReadModuleTimes(cleared).Should().Be(((DateTimeOffset?)null, (DateTimeOffset?)null));
    }

    [Theory]
    [InlineData("\"not-a-time\"")]
    [InlineData("{}")]
    [InlineData("42")]
    public void MalformedSerializedStateAlsoRefusesInsteadOfLosingThePin(string json)
    {
        var original = new TestSession();
        original.StateBag.SetValue<object>(AgentSessionModuleExtensions.SystemTimeKey,
            JsonSerializer.Deserialize<JsonElement>(json), JsonSerializerOptions.Default);
        var restored = new TestSession(AgentSessionStateBag.Deserialize(original.StateBag.Serialize()));
        var read = () => AgentSessionModuleExtensions.ReadModuleTimes(restored);
        read.Should().Throw<JsonException>();
    }

    private sealed class TestSession : AgentSession
    {
        public TestSession() { }
        public TestSession(AgentSessionStateBag stateBag) : base(stateBag) { }
    }

    private sealed class Observer : IContextContributor
    {
        public ContextRequest? Request { get; private set; }
        public ContextContributorDescriptor Descriptor { get; } =
            new("time-observer", new HashSet<string>(StringComparer.Ordinal) { "time.observed" });
        public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(true);
        public Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult<ContextSection?>(null);
        }
    }

    private sealed class Harness
    {
        internal TestSession Session { get; } = new();
        internal Observer Observer { get; } = new();
        internal IMemoryService Memory { get; } = Substitute.For<IMemoryService>();
        private readonly ExtensibleMemoryContextProvider _provider;
        internal Harness(bool withModules = true)
        {
            Session.WithMemoryIdentity("alex", "fresh-session", "fresh-conversation");
            Memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
                .Returns(new RecallResult { Context = new MemoryContext
                    { SessionId = "fresh-session", AssembledAtUtc = DateTimeOffset.UnixEpoch } });
            var isolation = Substitute.For<IMemoryIsolationPolicy>();
            isolation.ResolveReadScope(Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(),
                    Arg.Any<MemoryOperationAccess>())
                .Returns(call => MemoryScope.For(call.ArgAt<string?>(1) ?? "anonymous"));
            IContextContributor[] contributors = withModules ? [Observer] : [];
            var compiler = new ContextCompiler(contributors, isolation, TimeProvider.System,
                NullLogger<ContextCompiler>.Instance);
            _provider = new ExtensibleMemoryContextProvider(Memory,
                Substitute.For<IEmbeddingOrchestrator>(), Substitute.For<IClock>(), Substitute.For<IIdGenerator>(),
                Options.Create(new MemoryOptions()), Options.Create(new ContextFormatOptions()),
                Options.Create(new AgentFrameworkOptions()), NullLogger<Neo4jMemoryContextProvider>.Instance,
                compiler, contributors, NullLogger<ExtensibleMemoryContextProvider>.Instance);
        }
        internal async Task InvokeAsync()
        {
            await _provider.InvokingAsync(new AIContextProvider.InvokingContext(Substitute.For<AIAgent>(),
                Session, new AIContext { Messages = [new ChatMessage(ChatRole.User, "What was due then?")] }),
                CancellationToken.None);
        }
    }
}
