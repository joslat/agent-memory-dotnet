using System.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Resolution;

namespace AgentMemory.Core.Services;

internal sealed partial class MemoryContextAssembler
{
    /// <summary>
    /// RECALL-NAMESAKE-LEAK (<see cref="RecallOptions.NamesakeFilter"/>): drops the recalled facts and messages that are about
    /// a namesake of the one the question means (<see cref="NamesakeFilter"/>). One bounded read of who the items are about;
    /// nothing read when off, and nothing changed when the items name no two namesakes or the question picks out none.
    /// </summary>
    private async Task<(IReadOnlyList<Fact> Facts, IReadOnlyList<(Fact Fact, double Score)> FactScores,
        IReadOnlyList<Message> Messages, IReadOnlyList<(Message Message, double Score)> MessageScores)> DropNamesakesAsync(
        string question, IReadOnlyList<Fact> facts, IReadOnlyList<(Fact Fact, double Score)> factScores,
        IReadOnlyList<Message> messages, IReadOnlyList<(Message Message, double Score)> messageScores,
        MemoryScope scope, CancellationToken cancellationToken, TimeSpan? remaining)
    {
        var unchanged = (facts, factScores, messages, messageScores);
        if (string.IsNullOrWhiteSpace(question) || (facts.Count == 0 && messages.Count == 0)) return unchanged;
        if (remaining is { } left && left <= TimeSpan.Zero) return unchanged;
        using var expiry = remaining is not null ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        if (expiry is not null) expiry.CancelAfter(remaining!.Value);
        var token = expiry?.Token ?? cancellationToken;
        IReadOnlyList<RecalledItemEntity> links;
        try
        {
            links = await TimedAsync("memory.recall.namesakes", () => _longTerm.GetRecalledItemEntitiesAsync(
                facts.Select(f => f.FactId).ToList(), messages.Select(m => m.MessageId).ToList(), scope, token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (expiry is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
        {
            return unchanged;
        }
        cancellationToken.ThrowIfCancellationRequested();

        var (drop, droppedEntities) = NamesakeFilter.Decide(question, links);
        Activity.Current?.SetTag("memory.recall.namesakes.dropped_items", drop.Count);
        if (drop.Count == 0) return unchanged;
        Activity.Current?.SetTag("memory.recall.namesakes.dropped_entities", droppedEntities.Count);
        return (facts.Where(f => !drop.Contains(f.FactId)).ToList(),
            factScores.Where(s => !drop.Contains(s.Fact.FactId)).ToList(),
            messages.Where(m => !drop.Contains(m.MessageId)).ToList(),
            messageScores.Where(s => !drop.Contains(s.Message.MessageId)).ToList());
    }
}
