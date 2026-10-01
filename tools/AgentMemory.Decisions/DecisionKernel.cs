using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Decisions;

/// <summary>
/// S2, the decision kernel on core facts (PLAN 40.15; design: strategy/designs/decision-kernel.md). Each decision source is
/// written as one dated message into ordinary AgentMemory memory (one owner per corpus), in date order, so supersession
/// and corrections close what a later decision replaces. Answers come from recalled facts and their provenance.
/// </summary>
internal sealed class DecisionKernel : IDecisionRetriever
{
    private readonly IServiceProvider _services;
    private readonly string _owner;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<DecisionChunk>> _chunksOfSource;
    private readonly IReadOnlyList<(string Date, DateTimeOffset KnownAt)> _clock;
    private readonly int _facts;

    private DecisionKernel(IServiceProvider services, string owner, IReadOnlyDictionary<string, IReadOnlyList<DecisionChunk>> chunksOfSource,
        IReadOnlyList<(string Date, DateTimeOffset KnownAt)> clock, int facts)
    {
        _services = services;
        _owner = owner;
        _chunksOfSource = chunksOfSource;
        _clock = clock;
        _facts = facts;
    }

    /// <summary>The decision sources of a corpus: an ADR file is one, a release-notes entry is one.</summary>
    internal static IReadOnlyList<(string SourceId, string Date, IReadOnlyList<DecisionChunk> Chunks)> Sources(IReadOnlyList<DecisionChunk> chunks) =>
    [
        .. chunks
            .GroupBy(chunk => chunk.File.EndsWith("CHANGELOG.md", StringComparison.OrdinalIgnoreCase) ? $"{chunk.File}#{chunk.Heading}" : chunk.File)
            .Select(group => (SourceId: group.Key, Date: group.First().Date ?? "0000-00-00", Chunks: (IReadOnlyList<DecisionChunk>)group.ToList()))
            .Where(source => DateTime.TryParse(source.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            .OrderBy(source => source.Date, StringComparer.Ordinal)
            .ThenBy(source => source.SourceId, StringComparer.Ordinal),
    ];

    /// <summary>Replays a corpus into memory in date order; sources of one date run together.</summary>
    internal static async Task<DecisionKernel> BuildAsync(IServiceProvider services, string owner, IReadOnlyList<DecisionChunk> chunks,
        int concurrency, Action<string> log, CancellationToken cancellationToken)
    {
        var sources = Sources(chunks);
        var clock = new List<(string Date, DateTimeOffset KnownAt)>();
        using var gate = new SemaphoreSlim(concurrency);
        var done = 0;
        foreach (var day in sources.GroupBy(source => source.Date))
        {
            await Task.WhenAll(day.Select(async source =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { await IngestAsync(services, owner, source.SourceId, source.Date, source.Chunks, cancellationToken).ConfigureAwait(false); }
                finally { gate.Release(); }
            })).ConfigureAwait(false);
            clock.Add((day.Key, DateTimeOffset.UtcNow));
            done += day.Count();
            if (done % 25 < day.Count()) log($"    {owner}: {done}/{sources.Count} sources (through {day.Key})");
        }
        var chunksOfSource = sources.ToDictionary(source => source.SourceId, source => source.Chunks, StringComparer.Ordinal);
        return new DecisionKernel(services, owner, chunksOfSource, clock, sources.Count);
    }

    private static async Task IngestAsync(IServiceProvider services, string owner, string sourceId, string date,
        IReadOnlyList<DecisionChunk> chunks, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
        var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var session = $"{owner}-decisions";
        var at = DateTimeOffset.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        await shortTerm.AddConversationAsync(session, session, userId: owner, cancellationToken: cancellationToken).ConfigureAwait(false);
        var message = await shortTerm.AddMessageAsync(new Message
        {
            MessageId = sourceId,
            ConversationId = session,
            SessionId = session,
            Role = "user",
            Content = Content(date, chunks),
            TimestampUtc = at,
            Metadata = new Dictionary<string, object> { ["source"] = sourceId },
        }, cancellationToken).ConfigureAwait(false);
        await memory.ExtractAndPersistAsync(new ExtractionRequest { Messages = [message], SessionId = session, UserId = owner }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>What extraction reads: the decision, said as the decision it is, on its date.</summary>
    internal static string Content(string date, IReadOnlyList<DecisionChunk> chunks)
    {
        var text = new StringBuilder($"Decided on {date}.\n");
        // An ADR: its outcome first (what was decided), then its context; release notes: the entry.
        foreach (var chunk in chunks.OrderBy(c => c.Heading.Contains("Decision", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            if (text.Length > 6000) break;
            text.Append(chunk.Text).Append("\n\n");
        }
        return text.Length > 6000 ? text.ToString(0, 6000) : text.ToString();
    }

    public async Task<IReadOnlyList<DecisionChunk>> RetrieveAsync(string question, int top, CancellationToken cancellationToken) =>
        await RetrieveAsync(question, null, top, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Recalls the facts and returns their sources as chunks the answer model can cite, each carrying the facts it holds and
    /// their status. A dated question recalls as of that date, as the store knew it once everything decided by then was read.
    /// </summary>
    internal async Task<IReadOnlyList<DecisionChunk>> RetrieveAsync(string question, string? date, int top, CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MemoryOptions>>().Value.Recall with
        {
            MaxFacts = 40,
            MaxEntities = 0,
            MaxPreferences = 0,
            MaxRecentMessages = 0,
            MaxRelevantMessages = 0,
        };
        var request = new RecallRequest { SessionId = $"{_owner}-questions", UserId = _owner, Query = question, Options = options };
        RecallResult result;
        if (date is not null && DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var asOf))
        {
            var endOfDay = asOf.AddDays(1).AddTicks(-1);
            var knownAt = _clock.LastOrDefault(entry => string.CompareOrdinal(entry.Date, date) <= 0).KnownAt;
            result = await memory.RecallAsOfAsync(request, endOfDay, knownAt == default ? endOfDay : knownAt, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await memory.RecallAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var bySource = new List<(string SourceId, List<Fact> Facts)>();
        foreach (var fact in result.Context.RelevantFacts.Items)
        {
            foreach (var sourceId in fact.SourceMessageIds.Where(_chunksOfSource.ContainsKey))
            {
                var entry = bySource.FirstOrDefault(s => s.SourceId == sourceId);
                if (entry.SourceId is null) bySource.Add(entry = (sourceId, []));
                entry.Facts.Add(fact);
            }
        }

        return [.. bySource.Take(top).Select(source =>
        {
            var chunks = _chunksOfSource[source.SourceId];
            var cited = chunks.FirstOrDefault(c => c.Heading.Contains("Decision Outcome", StringComparison.OrdinalIgnoreCase)) ?? chunks[0];
            var header = cited.Text.Split('\n')[0];
            var lines = source.Facts.Select(f =>
                $"- {f.Subject} | {f.Predicate} | {f.Object}" + (f.ValidFrom is { } from ? $" (from {from:yyyy-MM-dd})" : "") +
                (f.ValidUntil is { } until && until < DateTimeOffset.UtcNow.AddDays(-1) ? $" (until {until:yyyy-MM-dd})" : ""));
            var excerpt = cited.Text.Length > 500 ? cited.Text[..500] + " …" : cited.Text;
            return cited with { Text = $"{header}\nWhat memory holds from this source:\n{string.Join('\n', lines)}\nExcerpt:\n{excerpt}" };
        })];
    }

    internal int SourceCount => _facts;

    internal IReadOnlyList<(string Date, DateTimeOffset KnownAt)> Clock => _clock;

    /// <summary>A kernel already replayed: its owner and its clock, from the manifest.</summary>
    internal static DecisionKernel Load(IServiceProvider services, string owner, IReadOnlyList<DecisionChunk> chunks,
        IReadOnlyList<(string Date, DateTimeOffset KnownAt)> clock)
    {
        var sources = Sources(chunks);
        return new DecisionKernel(services, owner, sources.ToDictionary(s => s.SourceId, s => s.Chunks, StringComparer.Ordinal), clock, sources.Count);
    }
}
