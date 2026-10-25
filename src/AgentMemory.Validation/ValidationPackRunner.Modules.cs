using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Validation;

public sealed partial class ValidationPackRunner
{
    /// <summary>
    /// The host's static module composition (40.26): the modules a module pack runs against, added as the host adds them;
    /// the runner binds them to the pack's Neo4j store. Without it a module pack fails its composition check.
    /// </summary>
    [Experimental("AMEXT001")]
    public Action<ModuleSetBuilder>? Modules { get; init; }

    /// <summary>Composes the host's modules into a pack's stack, with the pack as the closing judge.</summary>
    private bool ComposeModules(ValidationPack pack, IServiceCollection services, PackClosingJudge judge)
    {
        if (pack.Modules is null || Modules is not { } compose) return false;
        services.AddSingleton<IMemoryUpdateJudge>(judge);
        services.AddAgentMemoryModules(modules =>
        {
            AgentMemory.Extensibility.Neo4j.Neo4jModuleSetBuilderExtensions.UseNeo4jStore(modules);
            compose(modules);
        });
        return true;
    }

    /// <summary>Activates the composed modules and applies their migrations; activation problems are kept for the checks.</summary>
    private static async Task PrepareModulesAsync(PackStore store, CancellationToken cancellationToken)
    {
        ModuleSet set;
        try { set = store.Provider.GetRequiredService<ModuleSet>(); }
        catch (ModuleActivationException exception)
        {
            store.ModuleProblems = exception.Errors;
            return;
        }
        var runner = store.Provider.GetRequiredService<INeo4jTransactionRunner>();
        await new AgentMemory.Extensibility.Neo4j.Migrations.ModuleMigrator(runner, set.Resolution)
            .ApplyAsync("conformance", cancellationToken).ConfigureAwait(false);
        await runner.WriteAsync(async r => await r.RunAsync("CALL db.awaitIndexes(60)").ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>Runs every teach step through the real ingestion pipeline: admission, closings, transactions, versions.</summary>
    private static async Task TeachAsync(ValidationPack pack, PackStore store, PackClosingJudge judge, CancellationToken cancellationToken)
    {
        if (pack.Modules is not { } part || store.ModuleProblems is not null) return;
        var set = store.Provider.GetRequiredService<ModuleSet>();
        var time = new PackTime(store.Clock);
        foreach (var step in part.Teach)
        {
            var session = pack.Sessions.Single(s => s.Id == step.Session);
            var sources = store.Messages[step.Session];
            var at = step.At ?? session.Messages.Select(m => m.At).DefaultIfEmpty(store.Clock.Now).Last();
            store.Clock.Now = at;
            var proposals = step.Proposals.Select(p => p with
            {
                Source = p.Source with { MessageIds = [.. p.Source.MessageIds.Select(id => sources[int.Parse(id[1..], System.Globalization.CultureInfo.InvariantCulture)].Id)] },
            }).ToList();
            var trigger = Enum.Parse<IngestionTrigger>(step.Trigger);
            var descriptor = new IngestionProviderDescriptor { Id = step.Provider, Trigger = trigger, ExistingView = step.ExistingView };
            var module = set.Modules.First(m => ModuleIdentity.IsOwnedId(m.Manifest.Id, step.Provider)).Manifest.Id;
            judge.Answers = step.Closings.ToDictionary(
                c => c.Proposal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + c.Key, c => c.Probability, StringComparer.Ordinal);
            using var scope = store.Provider.CreateScope();
            var services = scope.ServiceProvider;
            var runner = services.GetRequiredService<INeo4jTransactionRunner>();
            var isolation = services.GetRequiredService<IMemoryIsolationPolicy>();
            try
            {
                var engine = new ModuleIngestion(set.Resolution, [new PackReplayProvider(descriptor, proposals)],
                    new Dictionary<string, IModuleStore>(StringComparer.Ordinal) { [module] = services.GetRequiredService<IModuleStore>() },
                    isolation, time, services.GetRequiredService<ILogger<ModuleIngestion>>(), new ModuleIngestionServices
                    {
                        Views = id => new ModuleViews(set.Resolution, new Neo4jModuleReader(runner, set.Resolution, id, isolation, time)),
                        Closings = services.GetRequiredService<IMemoryClosingApprover>(),
                    });
                store.Taught[step.Id] = await engine.IngestAsync(new IngestionRequest
                {
                    SessionId = $"{store.Prefix}-{session.Id}", Owner = session.Shared ? null : store.Owner(session.Owner), AtUtc = at,
                    Messages = [.. sources.Select(m => new IngestedMessage { Id = m.Id, Role = m.Role, Text = m.Text })],
                    Trigger = trigger, ProviderIds = trigger == IngestionTrigger.OnRequest ? [step.Provider] : [],
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
            {
                // A step the pipeline refuses to run (an undeclared existing view, a malformed recorded batch) is a failed check.
                store.TeachFailures[step.Id] = exception.Message;
            }
        }
        judge.Answers = new Dictionary<string, double>(StringComparer.Ordinal);
    }

    /// <summary>The module part's checks, each on its own: composition, versions, every teach outcome, record and view.</summary>
    private async Task ModuleChecksAsync(ValidationPack pack, PackStore store, List<PackCheckResult> checks, CancellationToken cancellationToken)
    {
        var part = pack.Modules!;
        if (Modules is null)
        {
            checks.Add(new("modules:composition", "modules", false, "the pack checks modules and the host composed none (ValidationPackRunner.Modules)"));
            return;
        }
        if (store.ModuleProblems is { } problems)
        {
            checks.AddRange(problems.Select((p, i) => new PackCheckResult($"modules:activation:{i + 1}", "modules", false, p)));
            return;
        }
        var set = store.Provider.GetRequiredService<ModuleSet>();
        foreach (var need in part.Require)
        {
            var active = set.Modules.FirstOrDefault(m => m.Manifest.Id == need.Id);
            var ok = active is not null && ModuleVersionRange.Parse(need.Version).Contains(active.Manifest.Version);
            checks.Add(new($"modules:require:{need.Id}", "modules", ok, active is null ? $"{need.Id} is not composed"
                : ok ? $"{need.Id} {active.Manifest.Version} is in {need.Version}" : $"{need.Id} {active.Manifest.Version} is not in {need.Version}"));
        }
        if (checks.Any(c => c.Kind == "modules" && !c.Passed)) return;

        foreach (var step in part.Teach)
        {
            if (store.TeachFailures.TryGetValue(step.Id, out var why))
            {
                checks.Add(new($"teach:{step.Id}", "teach", false, $"{step.Id} did not run: {why}"));
                continue;
            }
            var report = store.Taught[step.Id];
            var outcomes = report.Providers.SingleOrDefault()?.Proposals ?? [];
            if (report.Denied || report.Providers.SingleOrDefault()?.Status == ProviderIngestionStatus.Unavailable)
                checks.Add(new($"teach:{step.Id}", "teach", false, $"{step.Id}: {(report.Denied ? "denied by owner policy" : report.Providers.Single().Detail)}"));
            foreach (var expect in step.Expect)
            {
                var outcome = outcomes.FirstOrDefault(o => o.Index == expect.Proposal);
                var closed = outcome?.Closed ?? [];
                var ok = outcome is not null && outcome.Status.ToString() == expect.Status
                    && closed.Order(StringComparer.Ordinal).SequenceEqual(expect.Closed.Order(StringComparer.Ordinal));
                checks.Add(new($"teach:{step.Id}:{expect.Proposal}", "teach", ok, outcome is null
                    ? $"{step.Id} proposal {expect.Proposal}: no outcome"
                    : $"{step.Id} proposal {expect.Proposal}: {outcome.Status}{(closed.Count > 0 ? $", closed {string.Join(", ", closed)}" : "")}"
                      + (ok ? "" : $"; expected {expect.Status}{(expect.Closed.Count > 0 ? $", closed {string.Join(", ", expect.Closed)}" : "")}")
                      + (outcome.Errors.Count > 0 ? $" ({string.Join("; ", outcome.Errors)})" : "")));
            }
        }

        var time = new PackTime(store.Clock);
        var runner = store.Provider.GetRequiredService<INeo4jTransactionRunner>();
        var isolation = store.Provider.GetRequiredService<IMemoryIsolationPolicy>();
        Neo4jModuleReader Reader(string module) => new(runner, set.Resolution, module, isolation, time);

        foreach (var record in part.Records)
        {
            var read = await Reader(record.Module).FindAsync(new ModuleQuery
            {
                ModuleId = record.Module, Type = record.Type, Key = record.Key, Selection = ModuleRecordSelection.History, Limit = ModuleQuery.MaximumLimit,
            }, new ModuleReadContext { Scope = MemoryScope.For(store.Owner(record.Owner), includeShared: false) }, cancellationToken).ConfigureAwait(false);
            if (read.Status != ModuleReadStatus.Succeeded)
            {
                checks.Add(new($"records:{record.Id}", "records", false, $"{record.Id}: the read returned {read.Status} ({string.Join("; ", read.Errors)})"));
                continue;
            }
            var live = read.Records.FirstOrDefault(r => r.InvalidatedAtUtc is null);
            var state = live is not null ? "live" : read.Records.Count > 0 ? "closed" : "absent";
            var wrong = live is null ? [] : record.Properties
                .Where(p => !live.Properties.TryGetValue(p.Key, out var stored) || JsonSerializer.Serialize(stored) != JsonSerializer.Serialize(p.Value))
                .Select(p => p.Key).ToList();
            var versionsOk = record.Versions is not { } versions || read.Records.Count == versions;
            var ok = state == record.State && wrong.Count == 0 && versionsOk;
            checks.Add(new($"records:{record.Id}", "records", ok,
                $"{record.Owner}'s {record.Type} '{record.Key}' is {state} with {read.Records.Count} version(s)"
                + (ok ? "" : $"; expected {record.State}{(record.Versions is { } v ? $" with {v}" : "")}{(wrong.Count > 0 ? $", wrong {string.Join(", ", wrong)}" : "")}")));
        }

        var others = pack.Owners.ToDictionary(o => o, store.Owner, StringComparer.Ordinal);
        foreach (var view in part.Views)
        {
            var views = new ModuleViews(set.Resolution, Reader(view.Module));
            var context = new ModuleReadContext
            {
                Scope = MemoryScope.For(store.Owner(view.Owner)), SessionId = $"{store.Prefix}-view-{view.Id}", SystemAsOfUtc = view.SystemAsOf,
            };
            var result = view.Arguments is { } arguments
                ? await views.EvaluateWithArgumentsAsync(view.View, arguments, context, cancellationToken).ConfigureAwait(false)
                : await views.EvaluateAsync(view.View, context, cancellationToken).ConfigureAwait(false);
            if (result.Status != ModuleReadStatus.Succeeded)
            {
                checks.Add(new($"views:{view.Id}", "views", false, $"{view.Id}: the view returned {result.Status} ({string.Join("; ", result.Errors)})"));
                continue;
            }
            var keys = result.Items.Select(i => i.Record.Key).Order(StringComparer.Ordinal).ToList();
            var keysOk = keys.SequenceEqual(view.ExpectKeys.Order(StringComparer.Ordinal));
            checks.Add(new($"views:{view.Id}:keys", "views", keysOk,
                $"{view.Id}: returned [{string.Join(", ", keys)}]" + (keysOk ? "" : $", expected [{string.Join(", ", view.ExpectKeys.Order(StringComparer.Ordinal))}]")));
            var texts = result.Items.Select(i => i.Text).ToList();
            foreach (var (text, index) in view.RequiredText.Select((t, i) => (t, i)))
            {
                var found = texts.Any(t => t.Contains(text, StringComparison.Ordinal));
                checks.Add(new($"views:{view.Id}:required:{index + 1}", "views", found, $"{view.Id}: '{text}' {(found ? "rendered" : "not rendered")}"));
            }
            foreach (var (text, index) in view.ExcludedText.Select((t, i) => (t, i)))
            {
                var found = texts.Any(t => t.Contains(text, StringComparison.Ordinal));
                checks.Add(new($"views:{view.Id}:excluded:{index + 1}", "views", !found, $"{view.Id}: '{text}' {(found ? "rendered and should not be" : "not rendered")}"));
            }
            var foreign = result.Items.Where(i => i.Record.OwnerId is { } owner && owner != store.Owner(view.Owner)
                && others.Values.Contains(owner, StringComparer.Ordinal)).Select(i => i.Record.Key).ToList();
            checks.Add(new($"isolation:{view.Id}", "isolation", foreign.Count == 0, foreign.Count == 0
                ? $"{view.Id}: nothing of another owner's returned" : $"{view.Id}: another owner's records returned: {string.Join(", ", foreign)}"));
        }
    }

    /// <summary>Plays a provider: the step's recorded proposals, submitted to the real pipeline.</summary>
    private sealed class PackReplayProvider(IngestionProviderDescriptor descriptor, IReadOnlyList<ModuleProposal> proposals) : IIngestionProvider
    {
        public IngestionProviderDescriptor Descriptor { get; } = descriptor;

        public Task<IReadOnlyList<ModuleProposal>> ProposeAsync(IngestionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(proposals);
    }

    /// <summary>The pack's clock as the module SDK reads time.</summary>
    private sealed class PackTime(ReplayClock clock) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => clock.Now;
    }
}

/// <summary>
/// Plays the update judge for a module pack: the current teach step's recorded probabilities, keyed as module ingestion
/// asks (<c>proposal|key</c>). Core's closing approver, over the core writer's own policy and threshold, decides from them.
/// A pair without a recorded answer gets none, so it closes nothing.
/// </summary>
internal sealed class PackClosingJudge : IMemoryUpdateJudge
{
    public IReadOnlyDictionary<string, double> Answers { get; set; } = new Dictionary<string, double>(StringComparer.Ordinal);

    public bool IsEnabled => true;

    public Task<IReadOnlyDictionary<string, double>> JudgeAsync(MemoryUpdateRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, double>>(request.Pairs.Where(p => Answers.ContainsKey(p.Key))
            .ToDictionary(p => p.Key, p => Answers[p.Key], StringComparer.Ordinal));
}
