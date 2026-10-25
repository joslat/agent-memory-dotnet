using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Extensibility.AgentFramework;
using AgentMemory.Extensibility.Ingestion;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleTurnIngestionTests
{
    [Fact]
    public async Task RegisteringPipelineAloneDoesNotEnableWrites()
    {
        var seen = new List<IngestionRequest>(); var services = Services(false);
        services.AddSingleton<IModuleIngestion>(new Pipeline(r => seen.Add(r)));
        await using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = root.CreateScope(); await Invoke(scope.ServiceProvider, "hello");
        seen.Should().BeEmpty();
    }

    [Fact]
    public async Task ExplicitInlineActivationUsesActualSourcesAndBothRoles()
    {
        var seen = new List<IngestionRequest>(); var services = Services(false);
        services.AddAgentMemoryFrameworkModuleIngestion(_ => new Pipeline(r => seen.Add(r)));
        await using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = root.CreateScope(); await Invoke(scope.ServiceProvider, "hello");
        var request = seen.Single(); request.Owner.Should().Be("owner"); request.SessionId.Should().Be("session");
        request.Messages.Select(m => m.Role).Should().Equal("user", "assistant");
        request.Messages.Select(m => m.Text).Should().Equal("stored:hello", "stored:reply");
        request.Messages.Should().OnlyContain(m => m.Id.StartsWith("stored-"));
        request.Trigger.Should().Be(IngestionTrigger.EveryTurn); request.ProviderIds.Should().BeEmpty();
    }

    [Fact]
    public async Task RealQueuePreservesOrderAndCapturedIdentityAfterTurnScopesAreDisposed()
    {
        var services = Services(true); var seen = new List<(string Text, Guid Scope, string? Owner, string? App)>();
        services.AddScoped<Probe>();
        services.AddAgentMemoryFrameworkModuleIngestion(sp =>
        {
            var probe = sp.GetRequiredService<Probe>(); var context = sp.GetRequiredService<TestContext>();
            return new Pipeline(r => { probe.Disposed.Should().BeFalse(); seen.Add((r.Messages[0].Text, probe.Id, context.UserId, context.ApplicationId)); });
        });
        await using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var queue = root.GetRequiredService<IBackgroundExtraction>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TryEnqueue("owner:owner", token => release.Task.WaitAsync(token)).Should().BeTrue();
        var originalIds = new List<Guid>();
        try
        {
            foreach (var text in new[] { "first", "second" })
            {
                using var scope = root.CreateScope(); originalIds.Add(scope.ServiceProvider.GetRequiredService<Probe>().Id);
                await Invoke(scope.ServiceProvider, text);
            }
            seen.Should().BeEmpty();
        }
        finally { release.TrySetResult(); }
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        seen.Select(x => x.Text).Should().Equal("stored:first", "stored:second");
        seen.Should().OnlyContain(x => x.Owner == "owner" && x.App == "application-a" && !originalIds.Contains(x.Scope));
        seen.Select(x => x.Scope).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task NativeInvocationFailureDoesNotActivateFactory()
    {
        var services = Services(false); var calls = 0;
        services.AddAgentMemoryFrameworkModuleIngestion(_ => { calls++; return new Pipeline(_ => { }); });
        await using var root = services.BuildServiceProvider(); using var scope = root.CreateScope();
        await Invoke(scope.ServiceProvider, "failure", failed: true); calls.Should().Be(0);
    }

    [Fact]
    public async Task RejectedQueueDrainsEarlierWorkBeforeInlineFallback()
    {
        var services = Services(true); var queue = Substitute.For<IBackgroundExtraction>(); var drained = false; var calls = 0;
        queue.WhenIdleAsync("owner:owner", Arg.Any<CancellationToken>()).Returns(_ => { drained = true; return Task.CompletedTask; });
        services.AddSingleton(queue);
        services.AddAgentMemoryFrameworkModuleIngestion(_ => new Pipeline(_ => { drained.Should().BeTrue(); calls++; }));
        await using var root = services.BuildServiceProvider(); using var scope = root.CreateScope();
        await Invoke(scope.ServiceProvider, "fallback"); calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoreExtractionCompletesBeforeModuleIngestion(bool background)
    {
        var services = Services(background); var order = new List<string>();
        services.Configure<AgentFrameworkOptions>(o => o.AutoExtractOnPersist = true);
        services.AddAgentMemoryFrameworkModuleIngestion(_ => new Pipeline(_ => order.Add("module")));
        await using var root = services.BuildServiceProvider(validateScopes: true);
        root.GetRequiredService<IMemoryService>().ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("core"); return new ExtractionResult { SourceMessageIds = [] }; });
        using (var scope = root.CreateScope()) await Invoke(scope.ServiceProvider, "ordered");
        await root.GetRequiredService<IBackgroundExtraction>().WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        order.Should().Equal("core", "module");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModuleProvidersSeeWhatTheCoreWriterStoredAndClosedForTheTurn(bool background)
    {
        var services = Services(background); var seen = new List<IngestionRequest>();
        services.Configure<AgentFrameworkOptions>(o => o.AutoExtractOnPersist = true);
        services.AddAgentMemoryFrameworkModuleIngestion(_ => new Pipeline(seen.Add));
        await using var root = services.BuildServiceProvider(validateScopes: true);
        root.GetRequiredService<IMemoryService>().ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ExtractionResult
            {
                SourceMessageIds = [],
                Outcomes =
                [
                    new() { Kind = MemoryItemKind.Fact, Stage = IngestionStage.Persistence, Status = IngestionItemStatus.Succeeded,
                        SourceKey = "Marta uses the retry helper", PersistedId = "fact-2", Closed = ["fact-1"] },
                    new() { Kind = MemoryItemKind.Fact, Stage = IngestionStage.Embedding, Status = IngestionItemStatus.Succeeded, PersistedId = "fact-2" },
                    new() { Kind = MemoryItemKind.Preference, Stage = IngestionStage.Persistence, Status = IngestionItemStatus.Failed, SourceKey = "x" },
                ],
            });
        using (var scope = root.CreateScope()) await Invoke(scope.ServiceProvider, "core outcome");
        await root.GetRequiredService<IBackgroundExtraction>().WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var outcome = seen.Single().CoreOutcome!;
        outcome.Stored.Should().ContainSingle().Which.Should().Be(new CoreWrittenMemory { Id = "fact-2", Kind = "Fact", Text = "Marta uses the retry helper" });
        outcome.Closed.Should().Equal("fact-1");
    }

    [Fact]
    public async Task WithoutCoreExtractionTheCoreOutcomeIsUnknown()
    {
        var services = Services(false); var seen = new List<IngestionRequest>();
        services.AddAgentMemoryFrameworkModuleIngestion(_ => new Pipeline(seen.Add));
        await using var root = services.BuildServiceProvider(validateScopes: true);
        using (var scope = root.CreateScope()) await Invoke(scope.ServiceProvider, "no core");
        seen.Single().CoreOutcome.Should().BeNull();
    }

    [Fact]
    public async Task AFailedCoreWriteLeavesTheOutcomeUnknownAndModulesStillRun()
    {
        var services = Services(true); var seen = new List<IngestionRequest>();
        services.Configure<AgentFrameworkOptions>(o => o.AutoExtractOnPersist = true);
        services.AddAgentMemoryFrameworkModuleIngestion(_ => new Pipeline(seen.Add));
        await using var root = services.BuildServiceProvider(validateScopes: true);
        root.GetRequiredService<IMemoryService>().ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns<ExtractionResult>(_ => throw new InvalidOperationException("extractor down"));
        using (var scope = root.CreateScope()) await Invoke(scope.ServiceProvider, "core fails");
        await root.GetRequiredService<IBackgroundExtraction>().WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        seen.Single().CoreOutcome.Should().BeNull();
    }

    [Fact]
    public void TheCoreOutcomeIsPartOfTheRequestFingerprintOnlyWhenKnown()
    {
        var plain = new IngestionRequest { SessionId = "s" };
        System.Text.Json.JsonSerializer.Serialize(plain).Should().NotContain("CoreOutcome");
        System.Text.Json.JsonSerializer.Serialize(plain with { CoreOutcome = new() { Closed = ["f1"] } }).Should().Contain("CoreOutcome");
    }

    private static ServiceCollection Services(bool background)
    {
        var services = new ServiceCollection(); services.AddLogging();
        var memory = Substitute.For<IMemoryService>();
        memory.AddMessageWithIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(c => Stored(c.ArgAt<string>(0), c.ArgAt<string>(1), c.ArgAt<string>(2), c.ArgAt<string>(3)));
        memory.AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(c => Stored(c.ArgAt<string>(0), c.ArgAt<string>(1), c.ArgAt<string>(2), c.ArgAt<string>(3)));
        services.AddSingleton(memory); services.AddSingleton(Substitute.For<IEmbeddingOrchestrator>());
        services.AddSingleton(Substitute.For<IMemoryQueryFacade>()); services.AddSingleton(Substitute.For<IMemoryIsolationPolicy>());
        var clock = Substitute.For<IClock>(); clock.UtcNow.Returns(DateTimeOffset.UnixEpoch); services.AddSingleton(clock);
        var ids = Substitute.For<IIdGenerator>(); ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N")); services.AddSingleton(ids);
        services.AddScoped<TestContext>();
        services.AddScoped<IMemoryStoreContext>(sp => sp.GetRequiredService<TestContext>());
        services.AddScoped<IWritableMemoryStoreContext>(sp => sp.GetRequiredService<TestContext>());
        services.AddScoped<IWritableMemoryOwnerContext>(sp => sp.GetRequiredService<TestContext>());
        services.AddAgentMemoryFramework(o => { o.AutoExtractOnPersist = false; o.ExtractInBackground = background; });
        services.AddAgentMemoryFrameworkExtensibility(); return services;
    }
    private static Message Stored(string session, string conversation, string role, string text) => new()
    { MessageId = "stored-" + Guid.NewGuid().ToString("N"), SessionId = session, ConversationId = conversation, Role = role, Content = "stored:" + text, TimestampUtc = DateTimeOffset.UnixEpoch };
    private static async Task Invoke(IServiceProvider services, string text, bool failed = false)
    {
        var session = Substitute.For<AgentSession>(); session.WithMemoryIdentity("owner", "session", "conversation", "application-a");
        var provider = services.GetRequiredService<Neo4jMemoryContextProvider>(); var agent = Substitute.For<AIAgent>();
        var request = new[] { new ChatMessage(ChatRole.User, text) };
        var context = failed ? new AIContextProvider.InvokedContext(agent, session, request, new IOException("failed"))
            : new AIContextProvider.InvokedContext(agent, session, request, [new ChatMessage(ChatRole.Assistant, "reply")]);
        await provider.InvokedAsync(context, CancellationToken.None);
    }
    private sealed class TestContext : IWritableMemoryOwnerContext, IWritableMemoryStoreContext
    { public string? UserId { get; set; } public string? ApplicationId { get; set; } }
    private sealed class Probe : IDisposable
    { public Guid Id { get; } = Guid.NewGuid(); public bool Disposed { get; private set; } public void Dispose() => Disposed = true; }
    private sealed class Pipeline(Action<IngestionRequest> observe) : IModuleIngestion
    {
        public Task<IngestionReport> IngestAsync(IngestionRequest request, CancellationToken cancellationToken = default)
        { observe(request); return Task.FromResult(new IngestionReport()); }
        public Task<IngestionReport> ReplayAsync(IngestionRequest request, IReadOnlyList<RecordedProposals> recorded, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
