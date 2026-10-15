using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Examples.Commitments;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace Commitments.NativeChecks;

internal static partial class Program
{
    // Each invocation requires an empty disposable database and replays real native teaching only
    // through the frozen fork point. No module records are copied or seeded, and no database reset
    // is performed by this executable.
    private static async Task RunNegative(NativeHost host, Func<NativeHost> createControl, INeo4jTransactionRunner runner,
        JsonElement world, JsonElement negative, IReadOnlyDictionary<string, CapturedIngestion> captured, NativeRun coldBaseline, NativeRun baseline)
    {
        var id = Text(negative, "id");
        var first = world.GetProperty("teachSteps").EnumerateArray().Single(s => Text(s, "id") == "alex-open");
        var owner = Text(first, "owner"); var sourceSession = Text(first, "session");
        var anchor = Text(first.GetProperty("command").GetProperty("about"), "target");
        var before = await StoredProvenance(runner);
        Evidence.Add(new { kind = "negative-start", id, instruction = negative, before });
        host.Clock.UtcNow = host.Clock.UtcNow.AddSeconds(1);

        switch (id)
        {
            case "missing-source":
            case "foreign-source":
            {
                var original = captured["alex-open"].Request;
                var sourceId = id == "missing-source" ? "maf:unpersisted-fixture-source" : captured["sam-open"].Request.Messages.Single(m => m.Role == "user").Id;
                var request = original with { Messages = original.Messages.Select(m => m.Role == "user" ? m with { Id = sourceId } : m).ToArray() };
                // The real provider first records this invalid source claim with a valid request
                // fingerprint. Replaying that recording then reaches actual source membership checks.
                var report = await host.IngestAsync(request);
                var beforeReplayCalls = host.ProviderCalls;
                var replay = await host.ReplayAsync(new(request, report));
                Evidence.Add(new { kind = "source-refusal", id, request, report, replay });
                Require(id + "/original-source-refused", IsSourceRefusal(report));
                Require(id + "/recorded-source-refused", IsSourceRefusal(replay));
                Require(id + "/replay-does-not-regenerate", host.ProviderCalls == beforeReplayCalls);
                break;
            }
            case "wrong-owner-anchor":
            case "ambiguous-name":
            case "unknown-command-field":
            {
                var command = JsonNode.Parse(first.GetProperty("command").GetRawText())!.AsObject();
                if (id == "wrong-owner-anchor")
                    command["about"]!["target"] = world.GetProperty("entities").EnumerateArray().First(e => Text(e, "owner") != owner).GetProperty("id").GetString();
                else if (id == "ambiguous-name")
                {
                    var ambiguous = world.GetProperty("entities").EnumerateArray().Where(e => Text(e, "owner") == owner)
                        .GroupBy(e => Text(e, "name")).Single(g => g.Count() > 1).Key;
                    command["about"]!["kind"] = "name"; command["about"]!["target"] = ambiguous;
                }
                else command["unexpected_field"] = "reject this entire command";
                host.Selection = null;
                var run = await host.RunAsync(owner, sourceSession, Text(first, "conversation"), command.ToJsonString(), id + "-user", id + "-assistant");
                Evidence.Add(new { kind = "native-negative-command", id, run });
                Require(id + "/actual-provider-called", run.ProviderCalls == 1 && run.Ingestion.Count == 1);
                var reports = run.Ingestion.Select(i => i.Report).ToArray();
                if (id == "unknown-command-field")
                    Require(id + "/explicit-provider-error", reports.Length == 1 && reports[0].Providers.Count == 1
                        && reports[0].Providers[0].Status == ProviderIngestionStatus.Unavailable && reports[0].Recorded.Count == 0);
                else
                    Require(id + "/target-refused-after-valid-source", reports.Length == 1 && IsRefused(reports[0])
                        && !reports[0].Providers.SelectMany(p => p.Proposals).SelectMany(p => p.Errors).Any(e => e.StartsWith("Source message", StringComparison.Ordinal)));
                break;
            }
            case "assistant-command":
            {
                var command = JsonNode.Parse(first.GetProperty("command").GetRawText())!.AsObject();
                command["key"] = "assistant-only"; command["title"] = "Assistant-only instruction must not become memory";
                var text = command.ToJsonString(); host.Selection = null;
                var run = await host.RunAsync(owner, sourceSession, Text(first, "conversation"), "Please acknowledge.", id + "-user", id + "-assistant", assistantText: text);
                Evidence.Add(new { kind = "assistant-command", run });
                Require(id + "/assistant-actually-persisted", run.Ingestion.Count == 1 && run.Ingestion[0].Request.Messages.Any(m => m.Role == "assistant" && m.Text == text));
                Require(id + "/provider-ignores-assistant", run.ProviderCalls == 1 && run.Ingestion.Count == 1
                    && run.Ingestion[0].Report.Providers.Count == 1 && run.Ingestion[0].Report.Providers[0].Status == ProviderIngestionStatus.Succeeded
                    && run.Ingestion[0].Report.Recorded.All(r => r.Proposals.Count == 0) && run.Ingestion[0].Report.Providers.All(r => r.Proposals.Count == 0));
                break;
            }
            case "ownerless-everyturn":
            {
                var beforeQueries = host.Queries.Count; var beforeProviders = host.ProviderCalls;
                var report = await host.IngestAsync(captured["alex-open"].Request with { Owner = null });
                Evidence.Add(new { kind = "ownerless-refusal", report });
                Require(id + "/denied-before-provider", report.Denied && report.Providers.Count == 0 && report.Recorded.Count == 0 && host.ProviderCalls == beforeProviders);
                Require(id + "/no-database-call", host.Queries.Count == beforeQueries);
                break;
            }
            case "required-anchor-loss":
            {
                await DeleteAnchor(runner, anchor);
                // Use history without a link filter: otherwise a missing requested target could
                // hide a defect in the required-anchor check itself.
                var query = JsonSerializer.SerializeToElement(new { id, owner, view = "history", key = "draft", limit = 10 });
                var run = await Query(host, query, id);
                Evidence.Add(new { kind = "lost-anchor-query", run });
                Require(id + "/searched-empty", run.Envelopes.Count == 1 && run.Envelopes[0].Sections.Count == 0
                    && run.Envelopes[0].Omissions.Any(o => o.Reason == ContextOmissionReason.SearchedAndEmpty));
                Require(id + "/hidden-from-actual-client", run.Inputs.Count == 1 && !run.Inputs[0].AllText.Contains(Text(first.GetProperty("command"), "title"), StringComparison.Ordinal));
                break;
            }
            case "session-clear":
            {
                var query = world.GetProperty("queries").EnumerateArray().Single(q => Text(q, "id") == "open-after-correction");
                var beforeRead = await ReadKey(host, owner, "draft");
                using (var scope = host.Services.CreateScope())
                {
                    var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
                    Require(id + "/sources-existed", (await shortTerm.GetAllSessionMessagesAsync(sourceSession)).Count > 0);
                    await shortTerm.ClearSessionAsync(sourceSession, owner);
                    Require(id + "/sources-cleared", (await shortTerm.GetAllSessionMessagesAsync(sourceSession)).Count == 0);
                }
                var afterRead = await ReadKey(host, owner, "draft");
                var run = await Query(host, query, id);
                Evidence.Add(new { kind = "cleared-source-query", beforeRead, afterRead, run });
                Require(id + "/copied-provenance-retained", beforeRead.Status == ModuleReadStatus.Succeeded && beforeRead.Records.Count == 1
                    && afterRead.Status == ModuleReadStatus.Succeeded && JsonSerializer.Serialize(beforeRead.Records, Json) == JsonSerializer.Serialize(afterRead.Records, Json));
                CheckQuery(query, run);
                break;
            }
            case "missing-anchor-before-limit":
            {
                const string validAnchor = "negative-valid-anchor";
                const string validKey = "negative-valid-followup";
                const string validTitle = "Retain this valid follow-up";
                await runner.WriteAsync(async tx => await (await tx.RunAsync(
                    "CREATE (:Entity {id:$id,name:'Valid anchor',owner_id:$owner,created_at:datetime($at)})",
                    new { id = validAnchor, owner, at = host.Clock.UtcNow.ToString("O") })).ConsumeAsync());
                var command = JsonNode.Parse(first.GetProperty("command").GetRawText())!.AsObject();
                command["key"] = validKey; command["title"] = validTitle;
                command["due_at"] = host.Clock.UtcNow.AddMinutes(-1).ToString("O"); command["about"]!["target"] = validAnchor;
                host.Selection = null;
                var taught = await host.RunAsync(owner, sourceSession, Text(first, "conversation"), command.ToJsonString(), id + "-user", id + "-assistant");
                Evidence.Add(new { kind = "valid-slot-teaching", taught });
                Require(id + "/valid-record-actually-stored", taught.Ingestion.Count == 1 && taught.Ingestion[0].Report.Providers.SelectMany(p => p.Proposals).SingleOrDefault()?.Status == ProposalWriteStatus.Stored);
                var withAdded = await StoredProvenance(runner);
                Require(id + "/exactly-one-added-version", withAdded.Count == before.Count + 1);
                await DeleteAnchor(runner, anchor);
                var query = JsonSerializer.SerializeToElement(new { id, owner, view = "due", limit = 1 });
                var run = await Query(host, query, id);
                var records = run.Envelopes.SelectMany(e => e.Sections).Select(s => s.Metadata).OfType<ModuleViewResult>().SelectMany(v => v.Items).Select(i => i.Record).ToArray();
                Evidence.Add(new { kind = "valid-slot-query", run });
                Require(id + "/valid-slot-filled", records.Length == 1 && records[0].Key == validKey && records[0].OwnerId == owner);
                Require(id + "/valid-slot-provenance", records.Length == 1 && EqualIds(records[0].Source.MessageIds, ["maf:" + id + "-user"]));
                Require(id + "/valid-slot-reaches-client", run.Inputs.Count == 1 && run.Inputs[0].AllText.Contains(validTitle, StringComparison.Ordinal)
                    && !run.Inputs[0].AllText.Contains("Ask for Atlas review", StringComparison.Ordinal));
                var retained = await StoredProvenance(runner);
                Evidence.Add(new { kind = "valid-slot-retained-provenance", withAdded, retained });
                Require(id + "/records-retained-after-anchor-loss", SameProvenance(withAdded, retained));
                return;
            }
            case "off-neutrality":
            {
                var query = world.GetProperty("queries").EnumerateArray().Single(q => Text(q, "id") == "open-after-correction");
                var enabled = await Query(host, query, id + "-enabled"); CheckQuery(query, enabled);
                // The frozen case requires a fresh core-only host. Compare the same cache states:
                // teaching advances the world clock beyond the shared-corpus probe cache lifetime.
                await using var control = createControl();
                var coldDisabled = await Query(control, query, id + "-cold-disabled");
                var disabled = await Query(control, query, id + "-disabled");
                Evidence.Add(new { kind = "off-neutrality", coldBaseline, baseline, enabled, coldDisabled, disabled });
                Require(id + "/cold-module-not-invoked", coldDisabled.Ingestion.Count == 0 && coldDisabled.Envelopes.Count == 0 && coldDisabled.ProviderCalls == 0);
                Require(id + "/cold-core-input-neutrality", SameInput(coldBaseline, coldDisabled));
                Require(id + "/cold-core-query-shape-neutrality", coldBaseline.Queries.Order(StringComparer.Ordinal).SequenceEqual(coldDisabled.Queries.Order(StringComparer.Ordinal), StringComparer.Ordinal));
                Require(id + "/cold-no-module-query-or-ddl", !coldDisabled.Queries.Any(IsModuleQuery));
                Require(id + "/module-not-invoked", disabled.Ingestion.Count == 0 && disabled.Envelopes.Count == 0 && disabled.ProviderCalls == 0);
                Require(id + "/core-input-neutrality", SameInput(baseline, disabled));
                Require(id + "/core-query-shape-neutrality", baseline.Queries.Order(StringComparer.Ordinal).SequenceEqual(disabled.Queries.Order(StringComparer.Ordinal), StringComparer.Ordinal));
                Require(id + "/no-module-query-or-ddl", !disabled.Queries.Any(IsModuleQuery));
                Require(id + "/no-commitment-text", disabled.Inputs.Count == 1 && Strings(query, "requiredText").All(t => !disabled.Inputs[0].AllText.Contains(t, StringComparison.Ordinal)));
                break;
            }
            case "runner-mutation-control":
            {
                var query = world.GetProperty("queries").EnumerateArray().Single(q => Text(q, "id") == "open-after-correction");
                var run = await Query(host, query, id); CheckQuery(query, run);
                var mutated = JsonNode.Parse(query.GetRawText())!.AsObject(); mutated["expectedIds"] = new JsonArray("deliberately-wrong-record-id");
                var offset = Checks.Count;
                CheckQuery(JsonSerializer.SerializeToElement(mutated), run);
                var rejected = Checks.Skip(offset).ToArray(); Checks.RemoveRange(offset, Checks.Count - offset);
                Evidence.Add(new { kind = "expected-runner-failure", mutation = mutated, checks = rejected, run });
                Require(id + "/wrong-expectation-rejected", rejected.Any(c => !c.Passed && c.Name.EndsWith("/exact-record-identities", StringComparison.Ordinal)));
                var missing = run with { Envelopes = run.Envelopes.Select(e => e with { Sections = [] }).ToArray() };
                offset = Checks.Count; CheckQuery(query, missing);
                var omitted = Checks.Skip(offset).ToArray(); Checks.RemoveRange(offset, Checks.Count - offset);
                Evidence.Add(new { kind = "expected-missing-result-failure", checks = omitted });
                Require(id + "/omitted-result-rejected", omitted.Any(c => !c.Passed && c.Name.EndsWith("/exact-record-identities", StringComparison.Ordinal)));
                break;
            }
            default: throw new InvalidOperationException("Negative case has no implementation: " + id);
        }
        var after = await StoredProvenance(runner);
        Evidence.Add(new { kind = "negative-end", id, after });
        Require(id + "/no-version-or-provenance-change", SameProvenance(before, after));
    }

    private static bool IsRefused(IngestionReport report) => !report.Denied && report.Providers.Count == 1
        && report.Providers[0].Status == ProviderIngestionStatus.Refused && report.Providers[0].Proposals.Count == 1
        && report.Providers[0].Proposals[0].Status == ProposalWriteStatus.Refused;
    private static bool IsSourceRefusal(IngestionReport report) => IsRefused(report)
        && report.Providers[0].Proposals[0].Errors.Any(e => e.StartsWith("Source message is absent", StringComparison.Ordinal));
    private static bool IsModuleQuery(string query) => new[] { "CommitmentItem", "COMMITMENT_ABOUT", "ModuleKey", "ModuleMigration", "ModuleNamespace" }
        .Any(word => query.Contains(word, StringComparison.Ordinal));
    private static async Task DeleteAnchor(INeo4jTransactionRunner runner, string id)
        => await runner.WriteAsync(async tx => await (await tx.RunAsync("MATCH (n:Entity {id:$id}) DETACH DELETE n", new { id })).ConsumeAsync());
    private static async Task<ModuleReadResult> ReadKey(NativeHost host, string owner, string key)
    {
        using var scope = host.Services.CreateScope(); var sp = scope.ServiceProvider;
        var reader = new Neo4jModuleReader(sp.GetRequiredService<INeo4jTransactionRunner>(), host.Resolution,
            CommitmentsModule.Id, sp.GetRequiredService<IMemoryIsolationPolicy>(), host.Clock);
        return await reader.FindAsync(new() { ModuleId = CommitmentsModule.Id, Type = CommitmentsModule.Type, Key = key, Limit = 20 },
            new() { Scope = MemoryScope.For(owner) });
    }
    private sealed record StoredSource(string Id, string Owner, IReadOnlyList<string> MessageIds, string Excerpt);
    private static async Task<IReadOnlyList<StoredSource>> StoredProvenance(INeo4jTransactionRunner runner)
        => await runner.ReadAsync(async tx => (IReadOnlyList<StoredSource>)(await (await tx.RunAsync(
            "MATCH (n:CommitmentItem) RETURN n.id AS id,n.owner_id AS owner,n.source_message_ids AS sources,n.source_excerpt AS excerpt ORDER BY n.id")).ToListAsync())
            .Select(r => new StoredSource(r["id"].As<string>(), r["owner"].As<string>(), r["sources"].As<List<string>>(), r["excerpt"].As<string>())).ToArray());
    private static bool SameProvenance(IReadOnlyList<StoredSource> before, IReadOnlyList<StoredSource> after)
        => JsonSerializer.Serialize(before, Json) == JsonSerializer.Serialize(after, Json);
}
