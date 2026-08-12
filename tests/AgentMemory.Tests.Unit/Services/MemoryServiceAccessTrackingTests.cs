using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

public sealed class MemoryServiceAccessTrackingTests
{
    private readonly IShortTermMemoryService _shortTerm;
    private readonly IMemoryContextAssembler _assembler;
    private readonly IMemoryExtractionPipeline _extractionPipeline;
    private readonly IEntityRepository _entityRepository;
    private readonly IFactRepository _factRepository;
    private readonly IPreferenceRepository _preferenceRepository;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IMemoryDecayService _decayService;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly DateTimeOffset _fixedTime = new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    public MemoryServiceAccessTrackingTests()
    {
        _shortTerm = Substitute.For<IShortTermMemoryService>();
        _assembler = Substitute.For<IMemoryContextAssembler>();
        _extractionPipeline = Substitute.For<IMemoryExtractionPipeline>();
        _entityRepository = Substitute.For<IEntityRepository>();
        _factRepository = Substitute.For<IFactRepository>();
        _preferenceRepository = Substitute.For<IPreferenceRepository>();
        _embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
        _decayService = Substitute.For<IMemoryDecayService>();
        _clock = Substitute.For<IClock>();
        _idGenerator = Substitute.For<IIdGenerator>();

        _clock.UtcNow.Returns(_fixedTime);
        _idGenerator.GenerateId().Returns("generated-msg-id");

        _decayService
            .UpdateAccessTimestampAsync(Arg.Any<string>(), Arg.Any<MemoryNodeKind>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _decayService
            .UpdateAccessTimestampsAsync(
                Arg.Any<IReadOnlyCollection<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// The nodes handed to the single batched access-tracking call.
    /// </summary>
    /// <remarks>
    /// Recall now tracks access with one batched call instead of one call per recalled item — measured
    /// at 25 write transactions per default recall before the change, all on the pre-model path. These
    /// tests therefore assert on the batch's <em>contents</em>, which is the invariant that actually
    /// matters (every recalled item is tracked, under the right kind) rather than on how many method
    /// calls carried it.
    /// <para>
    /// Note that a substitute cannot exercise the interface's default implementation: the proxy replaces
    /// the default body, so <see cref="IMemoryDecayService.UpdateAccessTimestampAsync"/> is never reached
    /// through it. The fallback loop is covered where it runs for real, against the live adapter.
    /// </para>
    /// </remarks>
    private IReadOnlyCollection<(string NodeId, MemoryNodeKind NodeKind)> CapturedTrackedNodes()
    {
        var call = _decayService.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IMemoryDecayService.UpdateAccessTimestampsAsync));
        return (IReadOnlyCollection<(string NodeId, MemoryNodeKind NodeKind)>)call.GetArguments()[0]!;
    }

    private MemoryService CreateSut(IMemoryDecayService? decay = null, bool deferred = false) =>
        new(_shortTerm, _assembler, _extractionPipeline,
            _entityRepository, _factRepository, _preferenceRepository, _embeddingOrchestrator,
            Options.Create(new MemoryOptions { DeferAccessTracking = deferred }),
            _clock, _idGenerator,
            NullLogger<MemoryService>.Instance,
            decay);

    // ── 2.4: taking the bookkeeping write off the blocking path ──────────

    /// <summary>A context with one recalled fact, enough to trigger access tracking.</summary>
    private MemoryContext OneFactContext() => new()
    {
        SessionId = "s1",
        AssembledAtUtc = _fixedTime,
        RelevantFacts = new MemoryContextSection<Fact>
        {
            Items = new[]
            {
                new Fact
                {
                    FactId = "fact-1", Subject = "s", Predicate = "p", Object = "o",
                    Confidence = 0.9, CreatedAtUtc = _fixedTime,
                },
            },
        },
    };

    [Fact]
    public async Task DeferredAccessTrackingDoesNotBlockTheRecall()
    {
        // THE point. Access tracking feeds decay and retention; nothing in the returned context
        // depends on it, so the caller should not wait for a write burst before the model is invoked.
        // The substitute never completes, so a recall that returns proves it was not awaited.
        var pending = new TaskCompletionSource();
        var decay = Substitute.For<IMemoryDecayService>();
        decay.UpdateAccessTimestampsAsync(
                Arg.Any<IReadOnlyCollection<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>())
            .Returns(pending.Task);
        _assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneFactContext());

        var recall = CreateSut(decay, deferred: true)
            .RecallAsync(new RecallRequest { SessionId = "s1", Query = "q" });

        (await Task.WhenAny(recall, Task.Delay(TimeSpan.FromSeconds(5)))).Should().Be(recall,
            "the recall must return without waiting for the bookkeeping write");
        pending.SetResult();
    }

    [Fact]
    public async Task TheDefaultStillAwaitsTheWrite()
    {
        // The byte-identical guarantee: leaving the option off keeps failures and cancellation
        // observable on the calling path, which is why it was awaited in the first place.
        var completed = false;
        var decay = Substitute.For<IMemoryDecayService>();
        decay.UpdateAccessTimestampsAsync(
                Arg.Any<IReadOnlyCollection<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Delay(20);
                completed = true;
            });
        _assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneFactContext());

        await CreateSut(decay).RecallAsync(new RecallRequest { SessionId = "s1", Query = "q" });

        completed.Should().BeTrue("the default path awaits the write");
    }

    [Fact]
    public async Task ADeferredWriteIsDetachedFromTheRequestCancellationToken()
    {
        // The trap that would make this a no-op wearing a feature's name. The request token is
        // cancelled as soon as the response completes, so a deferred write that still carried it
        // would be cancelled by the very act of returning.
        CancellationToken captured = default;
        var decay = Substitute.For<IMemoryDecayService>();
        decay.UpdateAccessTimestampsAsync(
                Arg.Any<IReadOnlyCollection<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.ArgAt<CancellationToken>(1);
                return Task.CompletedTask;
            });
        _assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneFactContext());

        using var requestCts = new CancellationTokenSource();
        await CreateSut(decay, deferred: true)
            .RecallAsync(new RecallRequest { SessionId = "s1", Query = "q" }, requestCts.Token);
        await Task.Delay(50);
        requestCts.Cancel();

        captured.Should().Be(CancellationToken.None,
            "a deferred write must outlive the request that scheduled it");
    }

    [Fact]
    public async Task ADeferredFailureDoesNotFaultTheRecall()
    {
        // A bookkeeping write that throws must not surface as a failed recall -- and must not become
        // an unobserved task exception either, which is why the continuation logs it.
        var decay = Substitute.For<IMemoryDecayService>();
        decay.UpdateAccessTimestampsAsync(
                Arg.Any<IReadOnlyCollection<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("driver disposed"));
        _assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneFactContext());

        var act = async () => await CreateSut(decay, deferred: true)
            .RecallAsync(new RecallRequest { SessionId = "s1", Query = "q" });

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void DeferralIsOffByDefault()
    {
        // It is unsafe for a request-scoped host: the write runs on scoped services, and a scope
        // disposed when the response returns takes the driver session with it.
        new MemoryOptions().DeferAccessTracking.Should().BeFalse();
    }

    [Fact]
    public async Task RecallAsync_WithDecayService_UpdatesEntityAccess()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = _fixedTime,
            RelevantEntities = new MemoryContextSection<Entity>
            {
                Items = new[]
                {
                    CreateEntity("ent-1"),
                    CreateEntity("ent-2")
                }
            }
        };

        _assembler
            .AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(context));

        var sut = CreateSut(_decayService);
        await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "test" });

        CapturedTrackedNodes().Should().BeEquivalentTo(new[]
        {
            ("ent-1", MemoryNodeKind.Entity),
            ("ent-2", MemoryNodeKind.Entity),
        });
    }

    [Fact]
    public async Task RecallAsync_WithDecayService_TracksEveryCategoryInOneBatchedCall()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = _fixedTime,
            RelevantEntities = new MemoryContextSection<Entity> { Items = new[] { CreateEntity("ent-1") } },
            RelevantFacts = new MemoryContextSection<Fact> { Items = new[] { CreateFact("fact-1") } },
            RelevantPreferences = new MemoryContextSection<Preference> { Items = new[] { CreatePreference("pref-1") } },
        };

        _assembler
            .AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(context));

        var sut = CreateSut(_decayService);
        await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "test" });

        // Exactly one call — the regression guard for the 25-transactions-per-recall behaviour this
        // replaced. CapturedTrackedNodes() itself asserts singularity via Single().
        CapturedTrackedNodes().Should().BeEquivalentTo(new[]
        {
            ("ent-1", MemoryNodeKind.Entity),
            ("fact-1", MemoryNodeKind.Fact),
            ("pref-1", MemoryNodeKind.Preference),
        });
    }

    [Fact]
    public async Task RecallAsync_WithDecayService_UpdatesFactAccess()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = _fixedTime,
            RelevantFacts = new MemoryContextSection<Fact>
            {
                Items = new[] { CreateFact("fact-1") }
            }
        };

        _assembler
            .AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(context));

        var sut = CreateSut(_decayService);
        await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "test" });

        CapturedTrackedNodes().Should().BeEquivalentTo(new[] { ("fact-1", MemoryNodeKind.Fact) });
    }

    [Fact]
    public async Task RecallAsync_WithDecayService_UpdatesPreferenceAccess()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = _fixedTime,
            RelevantPreferences = new MemoryContextSection<Preference>
            {
                Items = new[] { CreatePreference("pref-1") }
            }
        };

        _assembler
            .AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(context));

        var sut = CreateSut(_decayService);
        await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "test" });

        CapturedTrackedNodes().Should().BeEquivalentTo(new[] { ("pref-1", MemoryNodeKind.Preference) });
    }

    [Fact]
    public async Task RecallAsync_WithoutDecayService_StillWorks()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = _fixedTime,
            RelevantEntities = new MemoryContextSection<Entity>
            {
                Items = new[] { CreateEntity("ent-1") }
            }
        };

        _assembler
            .AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(context));

        // No decay service
        var sut = CreateSut(null);
        var result = await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "test" });

        result.Should().NotBeNull();
        result.Context.RelevantEntities.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task RecallAsync_EmptyContext_NoAccessTracking()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = _fixedTime
        };

        _assembler
            .AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(context));

        var sut = CreateSut(_decayService);
        await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "test" });

        // An empty recall must not reach the database at all — not even to send an empty batch.
        await _decayService.DidNotReceive()
            .UpdateAccessTimestampAsync(Arg.Any<string>(), Arg.Any<MemoryNodeKind>(), Arg.Any<CancellationToken>());
        await _decayService.DidNotReceive()
            .UpdateAccessTimestampsAsync(
                Arg.Any<IReadOnlyCollection<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Entity CreateEntity(string id) => new()
    {
        EntityId = id,
        Name = $"Entity {id}",
        Type = "PERSON",
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    private static Fact CreateFact(string id) => new()
    {
        FactId = id,
        Subject = "Alice",
        Predicate = "works_at",
        Object = "ACME",
        Confidence = 0.8,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    private static Preference CreatePreference(string id) => new()
    {
        PreferenceId = id,
        Category = "style",
        PreferenceText = "Prefers dark mode",
        Confidence = 0.7,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };
}
