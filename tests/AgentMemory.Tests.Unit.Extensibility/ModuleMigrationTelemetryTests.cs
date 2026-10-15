using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Neo4j.Driver;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

// Controlled transaction boundaries prove instrumentation; real schema execution is covered separately.
public sealed class ModuleMigrationTelemetryTests
{
    [Fact]
    public async Task PlanningIsSetLevelAndParentsDatabaseWorkWithoutPrivateContent()
    {
        using var trace = new ModuleTraceCapture(); var runner = Substitute.For<INeo4jTransactionRunner>();
        var resolution = Resolve(); var plan = new ModuleMigrationPlan { Pending = ModuleMigrationDefinitions.Derive(resolution) };
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<ModuleMigrationPlan>>>(), Arg.Any<CancellationToken>())
            .Returns(_ => { using var db = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx"); return plan; });
        (await new ModuleMigrator(runner, resolution).PlanAsync()).Should().BeSameAs(plan);
        var span = trace.Stopped.Single(a => a.OperationName == "memory.module.migrate");
        span.ParentSpanId.Should().Be(trace.ParentSpanId);
        span.GetTagItem("memory.module.operation").Should().Be("plan");
        span.GetTagItem("memory.module.id").Should().BeNull();
        span.GetTagItem("memory.module.fingerprint").Should().Be(resolution.ManifestFingerprint);
        span.GetTagItem("memory.module.fingerprint.kind").Should().Be("metadata");
        span.GetTagItem("memory.module.outcome").Should().Be("Pending");
        span.GetTagItem("memory.module.migration.pending").Should().Be(plan.Pending.Count);
        trace.Stopped.Single(a => a.OperationName == "memory.db.tx").ParentSpanId.Should().Be(span.SpanId);
        runner.ReceivedCalls().Should().ContainSingle();
    }

    [Fact]
    public async Task RepeatedApplyReportsNoOpWithoutWritingOrInventingOwnedStepSpans()
    {
        using var trace = new ModuleTraceCapture(); var runner = Substitute.For<INeo4jTransactionRunner>();
        var resolution = Resolve(); var plan = new ModuleMigrationPlan { Applied = ModuleMigrationDefinitions.Derive(resolution) };
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<ModuleMigrationPlan>>>(), Arg.Any<CancellationToken>()).Returns(plan);
        (await new ModuleMigrator(runner, resolution).ApplyAsync("private-actor")).Should().BeSameAs(plan);
        var apply = trace.Stopped.Single(a => Equals(a.GetTagItem("memory.module.operation"), "apply"));
        apply.GetTagItem("memory.module.outcome").Should().Be("NoOp");
        apply.GetTagItem("memory.module.migration.applied").Should().Be(plan.Applied.Count);
        trace.Stopped.Single(a => Equals(a.GetTagItem("memory.module.operation"), "plan")).ParentSpanId.Should().Be(apply.SpanId);
        trace.Stopped.Should().HaveCount(2).And.OnlyContain(a => a.GetTagItem("memory.module.id") == null);
        runner.ReceivedCalls().Should().ContainSingle(); Safe(trace);
    }

    [Fact]
    public async Task OwnedStepFailureUsesItsActualModuleAndPropagatesWithoutPrivateDetails()
    {
        using var trace = new ModuleTraceCapture(); var runner = Substitute.For<INeo4jTransactionRunner>();
        var resolution = Resolve(); var step = ModuleMigrationDefinitions.Derive(resolution).First(s => s.ModuleId == "com.example.second");
        var plan = new ModuleMigrationPlan { Pending = [step] };
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<ModuleMigrationPlan>>>(), Arg.Any<CancellationToken>()).Returns(plan);
        runner.WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<ModuleMigrationPlan>>>(), Arg.Any<CancellationToken>()).Returns(plan);
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<ModuleLedgerEntry>>>(), Arg.Any<CancellationToken>())
            .Returns<ModuleLedgerEntry>(_ => { using var db = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx"); throw new IOException("private-database-error"); });
        var act = () => new ModuleMigrator(runner, resolution).ApplyAsync("private-actor");
        await act.Should().ThrowAsync<IOException>().WithMessage("private-database-error");
        var apply = trace.Stopped.Single(a => Equals(a.GetTagItem("memory.module.operation"), "apply"));
        var owned = trace.Stopped.Single(a => Equals(a.GetTagItem("memory.module.operation"), "step"));
        owned.ParentSpanId.Should().Be(apply.SpanId);
        owned.GetTagItem("memory.module.id").Should().Be("com.example.second");
        owned.GetTagItem("memory.module.version").Should().Be("2.0.0");
        owned.GetTagItem("memory.module.migration.steps").Should().Be(1, "one owned definition is attempted by this span, even when it fails");
        owned.GetTagItem("memory.module.outcome").Should().Be("Unavailable");
        apply.GetTagItem("memory.module.outcome").Should().Be("Unavailable");
        apply.GetTagItem("memory.module.id").Should().BeNull();
        owned.Status.Should().Be(ActivityStatusCode.Error);
        trace.Stopped.Single(a => a.OperationName == "memory.db.tx").ParentSpanId.Should().Be(owned.SpanId); Safe(trace);
    }

    [Fact]
    public async Task CancellationRemainsCancellationAndClosesPlanningSpan()
    {
        using var trace = new ModuleTraceCapture(); using var stop = new CancellationTokenSource();
        var runner = Substitute.For<INeo4jTransactionRunner>();
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<ModuleMigrationPlan>>>(), Arg.Any<CancellationToken>())
            .Returns<ModuleMigrationPlan>(_ => { stop.Cancel(); throw new OperationCanceledException("private-cancel", stop.Token); });
        var act = () => new ModuleMigrator(runner, Resolve()).PlanAsync(stop.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        var span = trace.Stopped.Should().ContainSingle().Subject;
        span.GetTagItem("memory.module.outcome").Should().Be("Cancelled"); span.Status.Should().Be(ActivityStatusCode.Error); Safe(trace);
    }

    [Fact]
    public async Task RefusedPlanDoesNotWriteOrLeakProblemText()
    {
        using var trace = new ModuleTraceCapture(); var runner = Substitute.For<INeo4jTransactionRunner>();
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<ModuleMigrationPlan>>>(), Arg.Any<CancellationToken>())
            .Returns(new ModuleMigrationPlan { Problems = ["private-schema-collision"] });
        var act = () => new ModuleMigrator(runner, Resolve()).ApplyAsync("private-actor");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("private-schema-collision");
        trace.Stopped.Single(a => Equals(a.GetTagItem("memory.module.operation"), "plan"))
            .GetTagItem("memory.module.outcome").Should().Be("Refused");
        runner.ReceivedCalls().Should().ContainSingle(); Safe(trace);
    }

    [Fact]
    public async Task EmptyCompositionAndInvalidActorEmitNoSpansOrDatabaseWork()
    {
        using var trace = new ModuleTraceCapture(); var runner = Substitute.For<INeo4jTransactionRunner>();
        var empty = ModuleResolver.Resolve([], ModuleVersion.Parse("0.1.0"), new HashSet<string>());
        var m = new ModuleMigrator(runner, empty);
        await m.PlanAsync(); await m.ApplyAsync("private-actor");
        var invalid = () => new ModuleMigrator(runner, Resolve()).ApplyAsync(" ");
        await invalid.Should().ThrowAsync<ArgumentException>();
        runner.ReceivedCalls().Should().BeEmpty(); trace.Stopped.Should().BeEmpty();
    }

    [Fact]
    public async Task SamplingNothingKeepsPlanningResultAndCallCount()
    {
        using var trace = new ModuleTraceCapture(ActivitySamplingResult.None); var runner = Substitute.For<INeo4jTransactionRunner>();
        var plan = new ModuleMigrationPlan();
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<ModuleMigrationPlan>>>(), Arg.Any<CancellationToken>()).Returns(plan);
        (await new ModuleMigrator(runner, Resolve()).PlanAsync()).Should().BeSameAs(plan);
        runner.ReceivedCalls().Should().ContainSingle(); trace.Stopped.Should().BeEmpty();
    }

    private static void Safe(ModuleTraceCapture trace)
    {
        trace.Stopped.Should().OnlyContain(a => ReferenceEquals(a.Source, AgentMemoryDiagnostics.Source));
        string.Join("|", trace.Stopped.SelectMany(a => a.TagObjects.Select(t => t.Value?.ToString())
            .Concat(a.Events.SelectMany(e => e.Tags.Select(t => t.Value?.ToString()))))).Should().NotContain("private-");
    }
    private static ModuleResolution Resolve() => ModuleResolver.Resolve([
        Manifest("first", "TraceFirst", "1.0.0"), Manifest("second", "TraceSecond", "2.0.0")
    ], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
    private static ModuleManifest Manifest(string id, string prefix, string version) => new()
    {
        Format = "agentmemory.module/v1alpha1", Id = "com.example." + id,
        Version = ModuleVersion.Parse(version), Sdk = ModuleVersionRange.Parse("[0.1,0.2)"),
        Ontology = new ModuleOntology { LabelPrefix = prefix, Types = [new ModuleType { Name = prefix + "Item" }] }
    };
}
