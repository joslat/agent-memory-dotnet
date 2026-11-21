#pragma warning disable AMEXT001
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// DIAG-PACK-FAILURES: a failed pack operation names its pack, its operation, the step that failed and a stable code: on the
/// exception (whose type and identity do not change), on its span and in one log line. Before, a host saw a sentence and
/// had to guess the step, and DemoBrain reported every failed pack import as a database that was down.
/// </summary>
public sealed class PackFailureTests
{
    private static KnowledgePack Pack() => KnowledgePack.Open(KnowledgePackTests.Signed(), KnowledgePackTests.Trust());

    private static IKnowledgePackStore NeverImported(KnowledgePack pack)
    {
        var store = Substitute.For<IKnowledgePackStore>();
        store.GetStateAsync(pack.Manifest, Arg.Any<CancellationToken>()).Returns(new KnowledgePackState { PackId = pack.Id });
        return store;
    }

    [Fact]
    public async Task A_downgrade_is_refused_at_validate_and_keeps_its_exception_type()
    {
        var pack = Pack();
        var store = Substitute.For<IKnowledgePackStore>();
        store.GetStateAsync(pack.Manifest, Arg.Any<CancellationToken>())
            .Returns(new KnowledgePackState { PackId = pack.Id, Version = "9.0.0", LiveRecords = 1 });

        var error = await FluentActions.Awaiting(() => new KnowledgePackImporter(store).ImportAsync(pack))
            .Should().ThrowExactlyAsync<KnowledgePackException>();

        PackFailure.Of(error.Which).Should().BeEquivalentTo(new PackFailure
        {
            Code = "pack.import.validate.refused", Operation = PackOperations.Import, Step = PackSteps.Validate,
            Kind = PackFailureKinds.Refused, PackId = pack.Id, Version = pack.Version, Digest = pack.Digest,
        });
        await store.DidNotReceiveWithAnyArgs().ImportAsync(default!, default, default);
    }

    [Fact]
    public async Task A_timeout_reading_the_store_is_unavailable_at_state()
    {
        var pack = Pack();
        var store = Substitute.For<IKnowledgePackStore>();
        store.GetStateAsync(pack.Manifest, Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("no answer"));

        var error = await FluentActions.Awaiting(() => new KnowledgePackImporter(store).ImportAsync(pack))
            .Should().ThrowExactlyAsync<TimeoutException>();

        PackFailure.Of(error.Which)!.Code.Should().Be("pack.import.state.unavailable");
    }

    [Fact]
    public async Task A_store_that_names_no_step_is_reported_at_store_and_the_same_exception_propagates()
    {
        var pack = Pack();
        var store = NeverImported(pack);
        var thrown = new InvalidOperationException("the store broke");
        store.ImportAsync(pack, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).ThrowsAsync(thrown);

        var error = await FluentActions.Awaiting(() => new KnowledgePackImporter(store).ImportAsync(pack))
            .Should().ThrowExactlyAsync<InvalidOperationException>();

        error.Which.Should().BeSameAs(thrown, "callers that catch the old exception keep working");
        PackFailure.Of(thrown)!.Code.Should().Be("pack.import.store.failed");
    }

    [Fact]
    public async Task The_deepest_step_names_the_failure()
    {
        var pack = Pack();
        var store = NeverImported(pack);
        store.ImportAsync(pack, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(_ =>
            PackOperationTrace.StepAsync<KnowledgePackImportResult>(PackOperations.Import, PackSteps.Schema, pack.Id, pack.Version, pack.Digest,
                () => throw new KnowledgePackException(["a constraint already exists with another shape"]), CancellationToken.None));

        var error = await FluentActions.Awaiting(() => new KnowledgePackImporter(store).ImportAsync(pack))
            .Should().ThrowExactlyAsync<KnowledgePackException>();

        PackFailure.Of(error.Which)!.Code.Should().Be("pack.import.schema.refused", "the schema step saw it before the importer did");
    }

    [Fact]
    public async Task A_cancellation_the_caller_asked_for_is_not_a_failure()
    {
        using var trace = new ModuleTraceCapture();
        var pack = Pack();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var store = Substitute.For<IKnowledgePackStore>();
        store.GetStateAsync(pack.Manifest, Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException(cancel.Token));

        var error = await FluentActions.Awaiting(() => new KnowledgePackImporter(store).ImportAsync(pack, cancellationToken: cancel.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        PackFailure.Of(error.Which).Should().BeNull();
        trace.Stopped.Single(a => a.OperationName == "memory.pack").GetTagItem("memory.pack.failure.code").Should().BeNull();
        trace.Stopped.Single(a => a.OperationName == "memory.pack").GetTagItem("memory.module.outcome").Should().Be("Cancelled");
    }

    [Fact]
    public async Task The_failure_is_on_the_operation_span_its_step_span_and_in_one_log_line()
    {
        using var trace = new ModuleTraceCapture();
        var log = new ListLogger<KnowledgePackImporter>();
        var pack = Pack();
        var store = NeverImported(pack);
        store.ImportAsync(pack, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(_ =>
            PackOperationTrace.StepAsync<KnowledgePackImportResult>(PackOperations.Import, PackSteps.Records, pack.Id, pack.Version, pack.Digest,
                () => throw new InvalidOperationException("write failed"), CancellationToken.None));

        await FluentActions.Awaiting(() => new KnowledgePackImporter(store, null, log).ImportAsync(pack)).Should().ThrowAsync<InvalidOperationException>();

        var span = trace.Stopped.Single(a => a.OperationName == "memory.pack");
        span.GetTagItem("memory.pack.operation").Should().Be("import");
        span.GetTagItem("memory.pack.id").Should().Be(pack.Id);
        span.GetTagItem("memory.pack.version").Should().Be(pack.Version);
        span.GetTagItem("memory.pack.digest").Should().Be(pack.Digest);
        span.GetTagItem("memory.pack.step.name").Should().Be("records");
        span.GetTagItem("memory.pack.failure.code").Should().Be("pack.import.records.failed");
        span.GetTagItem("memory.module.outcome").Should().Be("Failed");
        span.Status.Should().Be(System.Diagnostics.ActivityStatusCode.Error);
        var step = trace.Stopped.Single(a => a.OperationName == "memory.pack.step");
        step.ParentSpanId.Should().Be(span.SpanId);
        step.GetTagItem("memory.pack.failure.code").Should().Be("pack.import.records.failed");
        span.Tags.Concat(step.Tags).Select(t => t.Value).Should().NotContain(v => v != null && v.Contains("write failed"), "no exception text on spans");

        var line = log.Entries.Should().ContainSingle().Which;
        line.Level.Should().Be(LogLevel.Error);
        line.Message.Should().Contain(pack.Id).And.Contain("records").And.Contain("pack.import.records.failed");
        line.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task A_completed_import_writes_one_information_line_and_a_completed_span()
    {
        using var trace = new ModuleTraceCapture();
        var log = new ListLogger<KnowledgePackImporter>();
        var pack = Pack();
        var store = NeverImported(pack);
        store.ImportAsync(pack, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new KnowledgePackImportResult { PackId = pack.Id, Version = pack.Version, Digest = pack.Digest, Created = 3 });

        await new KnowledgePackImporter(store, null, log).ImportAsync(pack);

        log.Entries.Should().ContainSingle().Which.Should().Match<ListLogger<KnowledgePackImporter>.Entry>(e =>
            e.Level == LogLevel.Information && e.Message.Contains("import completed") && e.Message.Contains("3 new"));
        trace.Stopped.Single(a => a.OperationName == "memory.pack").GetTagItem("memory.module.outcome").Should().Be("Completed");
    }

    [Fact]
    public async Task A_failure_is_found_inside_wrapping_exceptions()
    {
        var inner = await FluentActions.Awaiting(() => PackOperationTrace.StepAsync(PackOperations.Detach, PackSteps.LocalCopy, "io.example.dojo", null, null,
            () => throw new InvalidOperationException("stale revision"), CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();

        PackFailure.Of(new InvalidOperationException("host wrapper", inner.Which))!.Code.Should().Be("pack.detach.local_copy.failed");
        PackFailure.Of(new AggregateException(new TimeoutException(), inner.Which))!.PackId.Should().Be("io.example.dojo");
        PackFailure.Of(new InvalidOperationException("unrelated")).Should().BeNull();
    }

    [Fact]
    public async Task A_database_outage_during_the_schema_step_is_unavailable_at_schema_not_a_generic_failure()
    {
        var pack = Pack();
        var runner = Substitute.For<INeo4jTransactionRunner>();
        // First read: the state (never imported). Second read: the schema step's own plan, when the database stops answering.
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<ModuleMigrationPlan>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ModuleMigrationPlan()), Task.FromException<ModuleMigrationPlan>(new ServiceUnavailableException("connection refused")));

        var error = await FluentActions.Awaiting(() => new KnowledgePackImporter(new Neo4jKnowledgePackStore(runner)).ImportAsync(pack))
            .Should().ThrowExactlyAsync<ServiceUnavailableException>();

        PackFailure.Of(error.Which)!.Code.Should().Be("pack.import.schema.unavailable");
    }

    [Fact]
    public async Task A_schema_the_migrator_refuses_on_a_reload_is_refused_at_schema()
    {
        var pack = Pack();
        var runner = Substitute.For<INeo4jTransactionRunner>();
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<ModuleMigrationPlan>>>(), Arg.Any<CancellationToken>())
            .Returns(new ModuleMigrationPlan(), new ModuleMigrationPlan { Problems = ["the stored statement hash of index X changed"] });

        var error = await FluentActions.Awaiting(() => new KnowledgePackImporter(new Neo4jKnowledgePackStore(runner)).ImportAsync(pack))
            .Should().ThrowExactlyAsync<InvalidOperationException>();

        PackFailure.Of(error.Which)!.Code.Should().Be("pack.import.schema.refused");
        error.Which.Message.Should().Contain("statement hash");
    }

    internal sealed class ListLogger<T> : ILogger<T>
    {
        public sealed record Entry(LogLevel Level, string Message, Exception? Exception);
        public List<Entry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
    }
}
