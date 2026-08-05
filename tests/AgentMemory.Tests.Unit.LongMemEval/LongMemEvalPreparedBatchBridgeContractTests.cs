using System.Reflection;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalPreparedBatchBridgeContractTests
{
    [Fact]
    public void PreparedBridge_ExposesExplicitExecutionAndAccountingContract()
    {
        var optionProperties = typeof(LongMemEvalAdapterOptions)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        optionProperties.Should().Contain(
        [
            "UseBatchedPreparation",
            "BatchExtractionPipeline",
            "BatchPlanner",
            "MaxSessionsPerBatch",
            "MaxInputTokens",
            "InitialQuestionNumber",
            "ExpectedExtractionPlan"
        ], "G3A.3 must explicitly consume the accepted deterministic batch path");

        typeof(LongMemEvalQuestionTelemetry)
            .GetProperty("ExtractionCallsPlanned")
            .Should().NotBeNull(
                "each frozen question must retain its exact preflight provider-call count");

        var manifestProperties = typeof(LongMemEvalPreparationManifest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        manifestProperties.Should().Contain(
        [
            "UseUnifiedExtraction",
            "UseMultiSessionBatchExtraction",
            "PreparationWorkers",
            "MaxSessionsPerBatch",
            "MaxInputTokens"
        ], "the prepared artifact fingerprint must identify the execution path");
        var preparedPairOptions = typeof(LongMemEvalPreparedPairProgram)
            .GetNestedType(
                "PreparedPairOptions",
                BindingFlags.NonPublic);
        preparedPairOptions.Should().NotBeNull();
        var preparedPairOptionProperties = preparedPairOptions!.GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        preparedPairOptionProperties.Should().Contain(
            "PreflightOnly",
            "a full live preparation cannot begin before a zero-provider-call frozen-plan gate");
        preparedPairOptionProperties.Should().Contain(
            [
                "CheckpointQuestions",
                "CheckpointTimeoutSeconds"
            ],
            "the bounded live checkpoint must be explicit and time-limited");

    }
}
