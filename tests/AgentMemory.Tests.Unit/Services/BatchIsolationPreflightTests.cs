using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

public sealed class BatchIsolationPreflightTests
{
    private readonly IExtractionStage _stage = Substitute.For<IExtractionStage>();
    private readonly IPersistenceStage _persistence = Substitute.For<IPersistenceStage>();
    private readonly IMultiSessionUnifiedMemoryExtractor _batch = Substitute.For<IMultiSessionUnifiedMemoryExtractor>();

    public BatchIsolationPreflightTests()
    {
        _stage.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<ExtractionTypes>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionStageResult());
        _stage.ProcessUnifiedAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<UnifiedExtractionResult>(), Arg.Any<ExtractionTypes>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(new ExtractionStageResult());
        _persistence.PersistAsync(Arg.Any<ExtractionStageResult>(), Arg.Any<string?>(), Arg.Any<MemoryTrustLevel>(), Arg.Any<CancellationToken>())
            .Returns(new PersistenceResult());
        _batch.ExtractAsync(Arg.Any<IReadOnlyList<ExtractionRequest>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(c => c.ArgAt<IReadOnlyList<ExtractionRequest>>(0).ToDictionary(r => r.SessionId, _ => new UnifiedExtractionResult()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_unscoped_request_in_strict_mode_prevents_all_provider_and_persistence_work(bool batch)
    {
        var policy = new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions { Mode = MemoryIsolationMode.StrictMultiTenant }),
            NullLogger<DefaultMemoryIsolationPolicy>.Instance);
        await Pipeline(batch, policy).Invoking(p => p.ExtractBatchAsync([Request("first", 1), Request("last", 2) with { UserId = null }], 2, 1000))
            .Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
        AssertNoWork();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Custom_read_or_write_rejection_of_late_request_prevents_all_work(bool batch, bool rejectWrite)
    {
        var policy = Substitute.For<IMemoryIsolationPolicy>();
        policy.ResolveReadScope(Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<MemoryOperationAccess>())
            .Returns(c => c.ArgAt<string?>(1) == "last" && !rejectWrite
                ? throw new InvalidOperationException("read refused") : MemoryScope.For(c.ArgAt<string>(1)));
        policy.ResolveWriteOwner(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<MemoryOperationAccess>())
            .Returns(c => c.ArgAt<string?>(0) == "last" && rejectWrite
                ? throw new InvalidOperationException("write refused") : c.ArgAt<string?>(0));
        await Pipeline(batch, policy).Invoking(p => p.ExtractBatchAsync([Request("first", 1), Request("last", 2)], 2, 1000))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage(rejectWrite ? "write refused" : "read refused");
        AssertNoWork();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_contradictory_shared_request_prevents_all_work(bool batch)
    {
        var policy = new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions()), NullLogger<DefaultMemoryIsolationPolicy>.Instance);
        await Pipeline(batch, policy).Invoking(p => p.ExtractBatchAsync([Request("first", 1), Request("last", 2) with { ShareWithEveryone = true }], 2, 1000))
            .Should().ThrowAsync<ArgumentException>();
        AssertNoWork();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Policy_is_resolved_once_per_request_before_work_and_its_values_are_reused(bool batch)
    {
        var policy = Substitute.For<IMemoryIsolationPolicy>();
        var scopes = new Dictionary<string, MemoryScope> { ["first"] = MemoryScope.For("read-first"), ["last"] = MemoryScope.For("read-last") };
        policy.ResolveReadScope(Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<MemoryOperationAccess>())
            .Returns(c => scopes[c.ArgAt<string>(1)]);
        policy.ResolveWriteOwner(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<MemoryOperationAccess>())
            .Returns(c => "write-" + c.ArgAt<string>(0));
        _batch.ExtractAsync(Arg.Any<IReadOnlyList<ExtractionRequest>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                policy.ReceivedCalls().Should().HaveCount(4, "both requests must be authorized before the provider receives any input");
                return c.ArgAt<IReadOnlyList<ExtractionRequest>>(0).ToDictionary(r => r.SessionId, _ => new UnifiedExtractionResult());
            });
        _stage.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<ExtractionTypes>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                policy.ReceivedCalls().Should().HaveCount(4);
                return new ExtractionStageResult();
            });
        using var stop = new CancellationTokenSource();
        await Pipeline(batch, policy).ExtractBatchAsync([Request("last", 2), Request("first", 1)], 2, 1000, stop.Token);
        foreach (var owner in new[] { "first", "last" })
        {
            var operation = batch ? "ExtractBatchAsync" : "ExtractAsync";
            policy.Received(1).ResolveReadScope(null, owner, operation, MemoryOperationAccess.Tenant);
            policy.Received(1).ResolveWriteOwner(owner, operation, MemoryOperationAccess.Tenant);
            if (batch)
                await _stage.Received(1).ProcessUnifiedAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<UnifiedExtractionResult>(),
                    ExtractionTypes.All, scopes[owner], stop.Token);
            else
                await _stage.Received(1).ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), ExtractionTypes.All, scopes[owner], stop.Token);
            await _persistence.Received(1).PersistAsync(Arg.Any<ExtractionStageResult>(), "write-" + owner, Arg.Any<MemoryTrustLevel>(), stop.Token);
        }
    }

    private void AssertNoWork()
    {
        _stage.ReceivedCalls().Should().BeEmpty();
        _persistence.ReceivedCalls().Should().BeEmpty();
        _batch.ReceivedCalls().Should().NotContain(c => c.GetMethodInfo().Name == "ExtractAsync");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_request_keeps_administrative_scope_and_preference_filtering(bool batch)
    {
        var staged = new ExtractionStageResult
        {
            FilteredPreferences = [new ExtractedPreference { Category = "food", PreferenceText = "likes coffee", Confidence = 1 }],
        };
        _stage.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<ExtractionTypes>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(staged);
        _stage.ProcessUnifiedAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<UnifiedExtractionResult>(), Arg.Any<ExtractionTypes>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(staged);
        var policy = new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions { Mode = MemoryIsolationMode.StrictMultiTenant }),
            NullLogger<DefaultMemoryIsolationPolicy>.Instance);
        await Pipeline(batch, policy).ExtractBatchAsync([Request("shared", 1) with { UserId = null, ShareWithEveryone = true }], 2, 1000);
        if (batch)
            await _stage.Received(1).ProcessUnifiedAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<UnifiedExtractionResult>(),
                ExtractionTypes.All, Arg.Is<MemoryScope?>(s => s != null && s.OwnerId == MemoryExtractionPipeline.SharedResolutionOwner && s.IncludeShared),
                Arg.Any<CancellationToken>());
        else
            await _stage.Received(1).ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), ExtractionTypes.All,
                Arg.Is<MemoryScope?>(s => s != null && s.OwnerId == MemoryExtractionPipeline.SharedResolutionOwner && s.IncludeShared), Arg.Any<CancellationToken>());
        await _persistence.Received(1).PersistAsync(Arg.Is<ExtractionStageResult>(s => s.FilteredPreferences.Count == 0),
            null, Arg.Any<MemoryTrustLevel>(), Arg.Any<CancellationToken>());
    }

    private MemoryExtractionPipeline Pipeline(bool batch, IMemoryIsolationPolicy policy)
    {
        _batch.IsEnabled.Returns(batch);
        return new(_stage, _persistence, NullLogger<MemoryExtractionPipeline>.Instance, policy, multiSessionExtractors: [_batch]);
    }

    private static ExtractionRequest Request(string owner, int minute) => new()
    {
        SessionId = owner, UserId = owner, Messages = [new Message
        {
            MessageId = owner, SessionId = owner, ConversationId = owner, Role = "user", Content = "My sister lives in Madrid.",
            TimestampUtc = DateTimeOffset.UnixEpoch.AddMinutes(minute),
        }],
    };
}
