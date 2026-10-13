using AgentMemory.Abstractions.Options;

namespace AgentMemory.RouterArena.Record;

/// <summary>Recommended memory with the benchmark's explicit history presentation choice.</summary>
internal static class BenchmarkMemoryOptions
{
    internal static MemoryOptions CreateRecommended(bool history)
    {
        var memory = MemoryOptions.CreateRecommended();
        return memory with { Projection = memory.Projection with { ResolveSupersessions = history } };
    }
}
