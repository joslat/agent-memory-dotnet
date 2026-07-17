namespace AgentMemory.Tests.Unit.Nams;

/// <summary>
/// Collection definition for every test that attaches a process-wide <see cref="System.Diagnostics.Metrics.MeterListener"/>
/// filtered by <see cref="AgentMemory.Nams.Observability.NamsMetrics.MeterName"/>. Mirrors
/// <c>AgentMemory.Tests.Unit.Observability.ObservabilityCollection</c>'s exact rationale: these tests share
/// process-global OpenTelemetry state, so they must not run concurrently with each other, or one test's
/// listener captures another's measurements.
/// </summary>
[CollectionDefinition("Nams Observability", DisableParallelization = true)]
public sealed class NamsObservabilityCollection;
