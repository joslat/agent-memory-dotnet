using System.Diagnostics;
using AgentMemory.Nams.Domain;

namespace AgentMemory.Nams.Observability;

/// <summary>
/// Builds the low-cardinality tag sets <see cref="NamsMetrics"/> instruments are recorded with. Every tag
/// value here is an enum-shaped string (backend name, operation name, status, failure kind) -- centralizing
/// construction keeps every call site tagging with the exact same key names, and keeps "never tag owner
/// IDs/memory text/prompts" (the plan's own rule) impossible to violate by accident at a call site.
/// </summary>
internal static class NamsMetricTags
{
    private const string Backend = "nams";

    public static TagList Operation(string operationName) => new()
    {
        { "backend", Backend },
        { "operation", operationName }
    };

    public static TagList OperationStatus(string operationName, string status) => new()
    {
        { "backend", Backend },
        { "operation", operationName },
        { "status", status }
    };

    public static TagList OperationFailureKind(string operationName, NamsFailureKind failureKind) => new()
    {
        { "backend", Backend },
        { "operation", operationName },
        { "failure_kind", failureKind.ToString() }
    };
}
