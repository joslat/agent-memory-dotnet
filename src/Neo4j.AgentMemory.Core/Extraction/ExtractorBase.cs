using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Core.Extraction;

public abstract class ExtractorBase<T>
{
    protected ILogger Logger { get; }

    protected ExtractorBase(ILogger logger)
    {
        Logger = logger;
    }

    protected abstract Task<IReadOnlyList<T>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken ct);

    public async Task<IReadOnlyList<T>> ExtractAsync(
        IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0) return Array.Empty<T>();
        try
        {
            return await ExtractCoreAsync(messages, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{ExtractorType} extraction failed; returning empty list.", GetType().Name);
            return Array.Empty<T>();
        }
    }
}
