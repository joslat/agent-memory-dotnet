using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Bundles;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;

#pragma warning disable AMEXT001 // Coding mode composes experimental memory packs, opt-in.

namespace AgentMemory.McpHost.Coding;

/// <summary>
/// Opt-in memory packs for coding mode (Extensibility 0.13 §M): the repository-experience pack, which writes lessons after each
/// remembered turn, and signed knowledge packs. Null composes no modules and keeps every tool's bytes as before.
/// </summary>
internal sealed record CodingModulesOptions
{
    /// <summary>Compose the repository-experience pack: a model writes lessons after each remembered turn.</summary>
    public bool Repository { get; init; }
    /// <summary>
    /// Compose the Coding composite (KP-composite): decisions, repository experience and the code map, decisions first. It
    /// includes the repository pack. Code map and decision records come from <c>codemap import</c> and <c>decisions import</c>.
    /// </summary>
    public bool Coding { get; init; }
    /// <summary>Signed knowledge packs to import into every project and search: bundle folders or .nupkg files, at most 8.</summary>
    public IReadOnlyList<string> Packs { get; init; } = [];
    /// <summary>Developer public keys (PEM files) trusted for packs besides the official key; at most 4.</summary>
    public IReadOnlyList<string> DeveloperKeys { get; init; } = [];
    /// <summary>At most this many module items a recall returns, 1 to 32.</summary>
    public int MaxItems { get; init; } = 8;

    public void Validate()
    {
        if (Packs is not { Count: <= 8 } || DeveloperKeys is not { Count: <= 4 } || MaxItems is < 1 or > 32
            || Packs.Concat(DeveloperKeys).Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 1024 || p.Any(char.IsControl))
            || (!Repository && !Coding && Packs.Count == 0))
            throw new ArgumentException("Coding modules need the coding or repository pack or 1-8 pack paths, at most 4 developer keys and 1-32 items.");
    }

    /// <summary>Opens and verifies every configured pack; a pack that is unsigned, changed or untrusted stops startup.</summary>
    public IReadOnlyList<KnowledgePack> OpenPacks()
    {
        var trust = new BundleTrust();
        foreach (var key in DeveloperKeys) trust.DeveloperKeys.Add(File.ReadAllText(key));
        return [.. Packs.Select(path =>
        {
            if (!path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)) return KnowledgePack.OpenDirectory(path, trust);
            using var package = File.OpenRead(path);
            return KnowledgePack.OpenPackage(package, trust);
        })];
    }
}

/// <summary>The packs a coding host opened at startup, for each project's import.</summary>
internal sealed record CodingPacks(IReadOnlyList<KnowledgePack> Packs);

/// <summary>One module memory a recall returns, beside the core context.</summary>
internal sealed record CodingModuleItem
{
    public required string Module { get; init; }
    public string? Type { get; init; }
    public string? Key { get; init; }
    public required string Text { get; init; }
    public double? Probability { get; init; }
    /// <summary>For a knowledge pack record, the pack version it came from.</summary>
    public string? Pack { get; init; }
    /// <summary>For a record traversal context reached, how it was reached from the anchors (CMP-1).</summary>
    public string? Via { get; init; }
}

/// <summary>A coding recall: the core result, and the module items when modules are composed.</summary>
internal sealed record CodingRecall(RecallResult Result, IReadOnlyList<CodingModuleItem>? Modules);

/// <summary>Module items are returned as data, like core memories in coding mode; clients fence what they put in a prompt.</summary>
internal sealed class CodingModuleRenderer : IModuleCandidateRenderer
{
    public bool Admit(ModuleContextCandidate candidate) => true;
    public string Render(IReadOnlyList<ModuleContextCandidate> candidates) => string.Join("\n", candidates.Select(c => c.Text));
}

internal static class CodingModuleRecall
{
    /// <summary>
    /// Core recall with module companions judged in the same gate call (Extensibility 0.12 R1, 0.13 S2): module views compile
    /// while core searches, a search view adds what core found to the question, and the kept items come back as data.
    /// </summary>
    public static async Task<CodingRecall> RecallAsync(IServiceProvider services, ModuleContextSelection selection, RecallRequest request,
        IReadOnlyList<ContextAnchor> anchors, CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource<IReadOnlyList<MemoryGateCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var companions = new RecallCompanions(pending.Task);
        var renderer = new CodingModuleRenderer();
        var compile = Task.Run(async () =>
        {
            var envelope = await services.GetRequiredService<IContextCompiler>().CompileAsync(new ContextRequest
            {
                SessionId = request.SessionId, Owner = request.UserId, Query = request.Query, CoreCandidates = companions.CoreCandidates,
                Anchors = anchors,
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (Envelope: envelope, Turn: selection.PrepareForCorePool(envelope, renderer, cancellationToken));
        }, cancellationToken);
        _ = compile.ContinueWith(t => pending.TrySetResult(t.IsCompletedSuccessfully ? t.Result.Turn.Companions : []),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        RecallResult result;
        try
        {
            result = await services.GetRequiredService<IMemoryRecall>().RecallAsync(request with { Companions = companions }, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Whatever core did, a module search never waits past it, and the module work is never left running unobserved.
            companions.OfferCoreCandidates([]);
            try { await compile.ConfigureAwait(false); } catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        }
        if (!compile.IsCompletedSuccessfully) return new(result, []);
        var metadata = result.Context.Metadata;
        var offered = metadata.TryGetValue(RecallCompanions.OfferedMetadataKey, out var sent) && sent is int count && count > 0;
        var answeredBy = result.Context.CompanionDecisions is null ? null
            : metadata.TryGetValue(RecallCompanions.JudgedByMetadataKey, out var judges) ? judges as string : null;
        var selected = compile.Result.Turn.Complete(result.Context.CompanionDecisions, answeredBy, offered, cancellationToken);
        return new(result, [.. selected.Decisions.Where(d => d.Disposition == ModuleCandidateDisposition.Kept).Select(d => new CodingModuleItem
        {
            Module = d.Candidate.Binding.ModuleId, Type = d.Candidate.Provenance?.RecordType, Key = d.Candidate.Provenance?.RecordKey,
            Text = d.Candidate.Text, Probability = d.Score,
            Pack = d.Candidate.Provenance?.SourcePack is { } pack ? pack.Id + "@" + pack.Version : null, Via = d.Candidate.Provenance?.Via,
        })]);
    }

    /// <summary>
    /// Module ingestion after a successful core write: the turn's messages, what core stored, and what the host knows about the
    /// turn (its commit, source and project), so a lesson is bound to the code version without asking a model.
    /// </summary>
    public static IngestionRequest Request(CodingIdentity identity, string session, IReadOnlyList<Message> messages,
        CodingRememberRequest turn, ExtractionResult core, DateTimeOffset atUtc) => new()
    {
        Owner = identity.OwnerId, SessionId = session, AtUtc = atUtc, Trigger = IngestionTrigger.EveryTurn,
        Messages = [.. messages.Where(m => m.Role is "user" or "assistant").Select(m => new IngestedMessage { Id = m.MessageId, Role = m.Role, Text = m.Content })],
        HostAttributes = new Dictionary<string, string>
        {
            ["commit"] = turn.Commit, ["source"] = turn.Source.Length <= 200 ? turn.Source : turn.Source[..200], ["repository"] = identity.ProjectId,
        },
        CoreOutcome = CoreWriteOutcome.From(core),
    };
}
