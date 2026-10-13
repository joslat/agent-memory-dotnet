using AgentMemory.Abstractions.Options;

namespace AgentMemory.RouterArena.Record;

/// <summary>Recommended memory with the benchmark's explicit history presentation choice.</summary>
internal static class BenchmarkMemoryOptions
{
    internal static MemoryOptions CreateRecommended(bool history)
    {
#pragma warning disable AMREC001
        var memory = MemoryOptions.CreateRecommended();
#pragma warning restore AMREC001
        return memory with { Projection = memory.Projection with { ResolveSupersessions = history } };
    }
}
