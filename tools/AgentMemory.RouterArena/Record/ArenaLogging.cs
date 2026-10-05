using Microsoft.Extensions.Logging;

namespace AgentMemory.RouterArena.Record;

/// <summary>
/// Every stack the arena builds logs here: warnings from everything, and what the gate and the update judge decide. A
/// failure inside the library (an embedding that could not be made, a judge that did not answer) reaches the output; it was
/// once only a warning no logger printed, and a run recorded without embeddings for hours (41.08).
/// </summary>
internal static class ArenaLogging
{
    public static void Console(ILoggingBuilder builder) =>
        builder.AddSimpleConsole(c => c.SingleLine = true)
            .SetMinimumLevel(LogLevel.Warning)
            .AddFilter("AgentMemory.Gate", LogLevel.Information)
            .AddFilter("AgentMemory.Core.Extraction.PersistenceStage", LogLevel.Information);
}
