namespace AgentMemory.Tests.Unit.Observability;

/// <summary>
/// Collection definition for every test that touches the <b>process-global</b> <c>MemoryActivitySource</c>
/// (and its <c>ActivityListener</c>s). These tests share static OpenTelemetry state, so they must not run
/// concurrently with each other <i>or</i> with any other collection — otherwise one test's global listener
/// captures another test's activities (and concurrent <c>List.Add</c> from listener callbacks corrupts the
/// capture). <c>DisableParallelization</c> runs the whole collection in isolation; every observability test
/// class carries <c>[Collection("Observability")]</c>.
/// </summary>
[CollectionDefinition("Observability", DisableParallelization = true)]
public sealed class ObservabilityCollection;
