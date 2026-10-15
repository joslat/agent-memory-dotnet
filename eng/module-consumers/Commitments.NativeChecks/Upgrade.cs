using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Examples.Commitments;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace Commitments.NativeChecks;

internal static partial class Program
{
    private static async Task Upgrade(NativeHost host, INeo4jTransactionRunner runner, JsonElement world,
        string primaryReceiptPath, string specPath)
    {
        var specBytes = await File.ReadAllBytesAsync(specPath);
        await File.WriteAllBytesAsync(Path.Combine(_output, "upgrade-spec.json"), specBytes);
        using var specification = JsonDocument.Parse(specBytes); var spec = specification.RootElement;
        var receiptBytes = await File.ReadAllBytesAsync(primaryReceiptPath);
        using var receipt = JsonDocument.Parse(receiptBytes); var primary = receipt.RootElement;
        if (Text(spec, "format") != "commitments-retained-upgrade/v1" || Text(spec, "worldSha256") != _worldHash
            || Text(spec, "fromVersion") != "1.0.0" || Text(spec, "toVersion") != "2.0.0"
            || CommitmentsModule.Manifest.Version.ToString() != "2.0.0"
            || Text(primary, "status") != "primary_passed_acceptance_incomplete" || Text(primary, "scenario") != "primary"
            || Text(primary, "worldSha256") != _worldHash || Text(primary, "packageVersion") != "1.0.0"
            || primary.GetProperty("failed").GetInt32() != 0 || primary.GetProperty("passed").GetInt32() == 0)
            throw new InvalidOperationException("Upgrade requires an exact successful v1 primary receipt, matching world/spec and actual v2 package.");
        var seal = primary.GetProperty("evidence").EnumerateArray()
            .Single(e => Text(e, "kind") == "retained-upgrade-snapshot").GetProperty("snapshot").GetString();
        var before = await SnapshotCommitments(runner);
        if (before != seal) throw new InvalidOperationException("Retained graph differs from successful v1 receipt; refusing migration.");
        var ids = await runner.ReadAsync(async tx => (await (await tx.RunAsync(
            "MATCH (n:CommitmentItem) RETURN n.id AS id ORDER BY id")).ToListAsync()).Select(r => r["id"].As<string>()).ToArray());
        if (!EqualIds(ids, Strings(spec, "expectedRetainedIds")))
            throw new InvalidOperationException("Retained version set differs from the frozen upgrade specification.");
        Require("upgrade/exact-v1-retained-graph", true);
        Evidence.Add(new { kind = "upgrade-inputs", primaryReceiptSha256 = Convert.ToHexString(SHA256.HashData(receiptBytes)).ToLowerInvariant(),
            specSha256 = Convert.ToHexString(SHA256.HashData(specBytes)).ToLowerInvariant(), before, ids });
        host.Clock.UtcNow = spec.GetProperty("atUtc").GetDateTimeOffset();
        var migrator = new ModuleMigrator(runner, host.Resolution, host.Clock);
        var plan = await migrator.PlanAsync();
        Require("upgrade/owned-additive-step-pending", plan.Problems.Count == 0 && plan.Pending.Count > 0 && plan.UndeclaredAppliedKeys.Count == 0);
        var applied = await migrator.ApplyAsync("commitments-native-v2-upgrade");
        Require("upgrade/all-owned-steps-applied", Settled(applied));
        var migrated = await SnapshotCommitments(runner);
        Require("upgrade/migration-preserves-all-record-properties-and-links", migrated == before);
        Require("upgrade/no-invented-priority-reason", await Count(runner,
            "MATCH (n:CommitmentItem) WHERE n.priority_reason IS NOT NULL RETURN count(n) AS n") == 0);
        var indexes = await runner.ReadAsync(async tx => (await (await tx.RunAsync(
            "SHOW INDEXES YIELD labelsOrTypes, properties WHERE labelsOrTypes = ['CommitmentItem'] AND properties = ['priority_reason'] RETURN count(*) AS n")).SingleAsync())["n"].As<long>());
        Require("upgrade/new-property-actually-indexed", indexes == 1);
        var firstState = await SnapshotAll(runner);
        var offset = host.Queries.Count;
        host.Clock.UtcNow = host.Clock.UtcNow.AddMinutes(1);
        var reapplied = await migrator.ApplyAsync("commitments-native-v2-second-apply");
        var secondQueries = host.Queries.ToArray().Skip(offset).ToArray();
        var secondState = await SnapshotAll(runner);
        Require("upgrade/second-apply-settled", Settled(reapplied));
        Require("upgrade/second-apply-preserves-entire-graph", firstState == secondState);
        Require("upgrade/second-apply-no-write-statements", secondQueries.All(q =>
            !System.Text.RegularExpressions.Regex.IsMatch(q, @"\b(CREATE|MERGE|SET|DELETE|DROP|REMOVE)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)));
        Evidence.Add(new { kind = "explicit-upgrade", plan, applied, reapplied, before, migrated, firstState, secondState, secondQueries });
        foreach (var query in world.GetProperty("queries").EnumerateArray().Where(q =>
                     q.TryGetProperty("systemAsOfUtc", out _) || Text(q, "id") is "bounded-history" or "other-owner" or "closed-not-outstanding" or "foreign-anchor"))
        {
            var run = await Query(host, query, "v2-retained-" + Text(query, "id"));
            Evidence.Add(new { kind = "v2-retained-query", id = Text(query, "id"), run });
            CheckQuery(query, run);
            Require("upgrade/" + Text(query, "id") + "/no-fabricated-reason", ViewRecords(run).All(r => !r.Properties.ContainsKey("priority_reason")));
        }
        var step = spec.GetProperty("teach"); host.Selection = null;
        var taught = await host.RunAsync(Text(step, "owner"), Text(step, "session"), Text(step, "conversation"),
            step.GetProperty("command").GetRawText(), Text(step, "userMessageId"), Text(step, "assistantMessageId"));
        Evidence.Add(new { kind = "v2-native-teaching", taught });
        Require("upgrade/one-native-ingestion", taught.Ingestion.Count == 1);
        if (taught.Ingestion.Count == 1) CheckOutcome(Text(step, "id"), step, taught.Ingestion[0].Report);
        var current = await ReadKey(host, Text(step, "owner"), Text(step.GetProperty("command"), "key"));
        Require("upgrade/new-reason-actually-stored", current.Status == ModuleReadStatus.Succeeded && current.Records.Count == 1
            && current.Records[0].Id == Text(step, "expectedRecordId")
            && current.Records[0].Properties.TryGetValue("priority_reason", out var reason)
            && reason.GetString() == Text(step.GetProperty("command"), "priority_reason"));
        Require("upgrade/new-version-provenance", current.Records.Count == 1
            && EqualIds(current.Records[0].Source.MessageIds, Strings(step, "expectedSourceIds")));
        Require("upgrade/package-and-record-version-distinct", CommitmentsModule.Manifest.Version.ToString() == "2.0.0"
            && current.Records.Count == 1 && current.Records[0].Id.EndsWith(":" + step.GetProperty("expectedVersion").GetInt32(), StringComparison.Ordinal));
        Evidence.Add(new { kind = "v2-typed-current", current });
        foreach (var key in new[] { "query", "history" })
        {
            var query = spec.GetProperty(key); var run = await Query(host, query, Text(query, "id"));
            Evidence.Add(new { kind = "v2-new-query", id = Text(query, "id"), run }); CheckQuery(query, run);
            Require("upgrade/" + key + "/reason-only-on-new-version", ViewRecords(run).All(r =>
                r.Id == Text(step, "expectedRecordId") ? r.Properties.TryGetValue("priority_reason", out var value)
                    && value.GetString() == Text(step.GetProperty("command"), "priority_reason") : !r.Properties.ContainsKey("priority_reason")));
        }
        foreach (var query in world.GetProperty("queries").EnumerateArray().Where(q =>
                     q.TryGetProperty("systemAsOfUtc", out _) || Text(q, "id") is "other-owner" or "foreign-anchor"))
        {
            var run = await Query(host, query, "v2-after-write-" + Text(query, "id"));
            Evidence.Add(new { kind = "v2-post-write-isolation-history", id = Text(query, "id"), run });
            CheckQuery(query, run);
            Require("upgrade/post-write/" + Text(query, "id") + "/reason-not-leaked", ViewRecords(run).All(r => !r.Properties.ContainsKey("priority_reason")));
        }
        Require("upgrade/exactly-one-added-version", await Count(runner, "MATCH (n:CommitmentItem) RETURN count(n) AS n") == ids.Length + 1);
    }

    private static IEnumerable<ModuleRecord> ViewRecords(NativeRun run) => run.Envelopes.SelectMany(e => e.Sections)
        .Select(s => s.Metadata).OfType<ModuleViewResult>().SelectMany(v => v.Items).Select(i => i.Record);
    private static bool Settled(ModuleMigrationPlan plan) => plan.Problems.Count == 0 && plan.Pending.Count == 0
        && plan.PendingNamespaces.Count == 0 && plan.PendingBookkeeping.Count == 0;
    // Snapshot every persisted property and outgoing link, including version/provenance metadata.
    // No final module record is seeded, copied, reset or repaired by the upgrade runner.
    private static Task<string> SnapshotCommitments(INeo4jTransactionRunner runner) => Snapshot(runner,
        "MATCH (n:CommitmentItem) RETURN properties(n) AS node UNION ALL MATCH (n:CommitmentItem)-[r]->(t) RETURN {source:n.id,relation:type(r),target:t.id,properties:properties(r)} AS node");
    private static Task<string> SnapshotAll(INeo4jTransactionRunner runner) => Snapshot(runner,
        "MATCH (n) RETURN {labels:labels(n),properties:properties(n)} AS node UNION ALL MATCH (n)-[r]->(t) RETURN {source:elementId(n),relation:type(r),target:elementId(t),properties:properties(r)} AS node");
    private static async Task<string> Snapshot(INeo4jTransactionRunner runner, string query)
    {
        var rows = await runner.ReadAsync(async tx => await (await tx.RunAsync(query)).ToListAsync());
        return JsonSerializer.Serialize(rows.Select(r => Stable(r["node"])).Order(StringComparer.Ordinal).ToArray(), Json);
    }
    private static string Stable(object? value) => value switch
    {
        null => "null",
        string text => "string:" + JsonSerializer.Serialize(text),
        IReadOnlyDictionary<string, object> map => "map:" + string.Join(";", map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + "=" + Stable(p.Value))),
        IDictionary<string, object> map => "map:" + string.Join(";", map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + "=" + Stable(p.Value))),
        IEnumerable sequence => "list:[" + string.Join(",", sequence.Cast<object?>().Select(Stable)) + "]",
        IFormattable scalar => value.GetType().FullName + ":" + scalar.ToString(null, CultureInfo.InvariantCulture),
        _ => value.GetType().FullName + ":" + value
    };
}
