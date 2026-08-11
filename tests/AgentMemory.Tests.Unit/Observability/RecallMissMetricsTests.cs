using System.Diagnostics.Metrics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Observability;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Observability;

/// <summary>
/// A recall that came back with nothing must leave a record.
/// </summary>
/// <remarks>
/// <para>
/// <b>The misses are the roadmap, and nothing recorded them.</b> A <c>:MemoryReadAudit</c> row is
/// created inside <c>MATCH (n:{label} {id: $id})</c>, so a row exists only for a <b>hit</b>. There was
/// no record anywhere that an owner asked and memory had nothing.
/// </para>
/// <para>
/// A counter, not a stored node: a node per miss grows without bound on exactly the workload that
/// produces the most misses.
/// </para>
/// </remarks>
[Collection("Observability")]
public sealed class RecallMissMetricsTests
{
    private static MemoryContextSection<T> Section<T>(
        bool searched, int limit, int returned, IReadOnlyList<T> items) =>
        new()
        {
            Items = items,
            Diagnostics = new MemoryContextSectionDiagnostics(searched, limit, returned, null, null, 0.7),
        };

    private static (IMemoryService Sut, List<(string Name, long Value, string? Category)> Measurements, MeterListener Listener)
        Create(MemoryContext context)
    {
        var inner = Substitute.For<IMemoryService>();
        inner.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RecallResult { Context = context });

        var measurements = new List<(string, long, string?)>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name.StartsWith("memory.recall.section.", StringComparison.Ordinal))
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? category = null;
            foreach (var tag in tags)
                if (tag.Key == "memory.category") category = tag.Value?.ToString();
            measurements.Add((instrument.Name, value, category));
        });
        listener.Start();

        return (new InstrumentedMemoryService(inner, new MemoryMetrics()), measurements, listener);
    }

    [Fact]
    public async Task ASearchedButEmptyFactSectionIsCounted()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = DateTimeOffset.UnixEpoch,
            RelevantFacts = Section(searched: true, limit: 10, returned: 0, Array.Empty<Fact>()),
        };

        var (sut, measurements, listener) = Create(context);
        using (listener)
        {
            await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "q" });
            listener.RecordObservableInstruments();
        }

        measurements.Should().Contain(m =>
            m.Name == "memory.recall.section.empty" && m.Category == "facts" && m.Value == 1);
    }

    [Fact]
    public async Task ASectionThatWasNeverSearchedIsNotCountedAsAMiss()
    {
        // The distinction the whole instrument exists for. A section excluded by a recall policy says
        // nothing about the store, and counting it would inflate the miss rate with turns that never
        // asked -- which would make the metric actively misleading rather than merely incomplete.
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = DateTimeOffset.UnixEpoch,
            RelevantFacts = Section(searched: false, limit: 0, returned: 0, Array.Empty<Fact>()),
        };

        var (sut, measurements, listener) = Create(context);
        using (listener)
        {
            await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "q" });
        }

        measurements.Should().NotContain(m => m.Category == "facts");
    }

    [Fact]
    public async Task NothingIsEmittedWithoutDiagnostics()
    {
        // Diagnostics are off by default. Emitting a guess would be worse than emitting nothing:
        // "empty" and "never searched" are different, and only the diagnostics can tell them apart.
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = DateTimeOffset.UnixEpoch,
        };

        var (sut, measurements, listener) = Create(context);
        using (listener)
        {
            await sut.RecallAsync(new RecallRequest { SessionId = "s1", Query = "q" });
        }

        measurements.Should().BeEmpty();
    }
}
