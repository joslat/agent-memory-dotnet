using System.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Observability;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Observability;

/// <summary>
/// A tenant identifier must not reach telemetry unless the host asks for it.
/// </summary>
/// <remarks>
/// <para>
/// A trace backend is usually less access-controlled than the database the value came from, retained
/// on a different schedule, and often exported to a third party. The rest of this codebase already
/// takes that position: every owner-scoped vector search tags <c>memory.vector.owner_scoped</c> as a
/// <b>boolean</b>, never the identifier.
/// </para>
/// <para>
/// The observability decorator was the one place that did the opposite, emitting
/// <c>memory.user_id</c> unconditionally.
/// </para>
/// </remarks>
[Collection("Observability")]
public sealed class TelemetryOwnerPrivacyTests
{
    private static (IMemoryService Sut, List<Activity> Activities, IDisposable Listener) Create(
        bool includeOwnerId)
    {
        var inner = Substitute.For<IMemoryService>();
        inner.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RecallResult
            {
                Context = new MemoryContext { SessionId = "s1", AssembledAtUtc = DateTimeOffset.UnixEpoch },
            });

        var activities = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MemoryActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var sut = new InstrumentedMemoryService(
            inner,
            new MemoryMetrics(),
            extractionOptions: null,
            observabilityOptions: Options.Create(new ObservabilityOptions
            {
                IncludeOwnerIdInTelemetry = includeOwnerId,
            }));

        return (sut, activities, listener);
    }

    [Fact]
    public async Task ByDefaultTheRawOwnerIdNeverReachesASpanTag()
    {
        var (sut, activities, listener) = Create(includeOwnerId: false);
        using (listener)
        {
            await sut.RecallAsync(new RecallRequest
            {
                SessionId = "s1",
                Query = "q",
                UserId = "tenant-secret-42",
            });
        }

        activities.Should().NotBeEmpty();
        activities.SelectMany(a => a.TagObjects)
            .Should().NotContain(tag => Equals(tag.Value, "tenant-secret-42"),
                "a tenant identifier in a trace is tenant data in a less access-controlled system");
    }

    [Fact]
    public async Task ByDefaultTheOwnerDimensionIsStillObservableAsABoolean()
    {
        // Removing the value must not remove the signal: an operator still needs to tell a scoped
        // operation from an unscoped one, which is the actual diagnostic question.
        var (sut, activities, listener) = Create(includeOwnerId: false);
        using (listener)
        {
            await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "q", UserId = "tenant-a" });
        }

        // TagObjects, not Tags: Activity.Tags surfaces only string-valued tags, and this one is a bool
        // -- deliberately, matching memory.vector.owner_scoped elsewhere in the codebase.
        activities.SelectMany(a => a.TagObjects)
            .Should().Contain(tag => tag.Key == "memory.owner_scoped" && Equals(tag.Value, true));
    }

    [Fact]
    public async Task AHostThatOptsInStillGetsTheIdentifier()
    {
        // Deliberately still reachable. Some hosts correlate traces by user and have decided their
        // identifier is safe to export; the point is that it is now a decision, not a default.
        var (sut, activities, listener) = Create(includeOwnerId: true);
        using (listener)
        {
            await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "q", UserId = "tenant-a" });
        }

        activities.SelectMany(a => a.TagObjects)
            .Should().Contain(tag => tag.Key == "memory.user_id" && Equals(tag.Value, "tenant-a"));
    }
}
