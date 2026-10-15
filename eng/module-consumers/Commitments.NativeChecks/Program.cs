using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.Examples.Commitments;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace Commitments.NativeChecks;

internal static partial class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly List<object> Evidence = [];
    private static readonly List<Check> Checks = [];
    private static string _output = "";
    private static string _worldHash = "";
    private static string _scenario = "primary";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 4 || args.Length % 2 != 0 || args[0] != "--world" || args[2] != "--output")
            throw new ArgumentException("Expected --world <json> --output <new-directory> [--case <id>] or [--upgrade-from <primary-receipt> --upgrade-spec <json>].");
        var options = args.Chunk(2).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        if (options.Keys.Any(k => k is not ("--world" or "--output" or "--case" or "--upgrade-from" or "--upgrade-spec"))
            || options.ContainsKey("--upgrade-from") != options.ContainsKey("--upgrade-spec")
            || (options.ContainsKey("--case") && options.ContainsKey("--upgrade-from")))
            throw new ArgumentException("Unknown or incompatible scenario options.");
        _scenario = options.GetValueOrDefault("--case", options.ContainsKey("--upgrade-from") ? "upgrade-v2" : "primary");
        _output = Path.GetFullPath(args[3]);
        if (Directory.Exists(_output)) throw new IOException("Evidence output already exists; refusing overwrite.");
        Directory.CreateDirectory(_output);
        try
        {
            var bytes = await File.ReadAllBytesAsync(args[1]);
            _worldHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await File.WriteAllBytesAsync(Path.Combine(_output, "world.json"), bytes);
            using var document = JsonDocument.Parse(bytes); var world = document.RootElement;
            if (world.GetProperty("module").GetString() != CommitmentsModule.Id)
                throw new InvalidOperationException("World does not target the loaded package.");
            JsonElement? negative = _scenario is "primary" or "upgrade-v2" ? null : world.GetProperty("negativeCases").EnumerateArray()
                .Single(n => Text(n, "id") == _scenario);
            var uri = Environment.GetEnvironmentVariable("NEO4J_URI") ?? "";
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var address) || !address.IsLoopback
                || Environment.GetEnvironmentVariable("AGENTMEMORY_DISPOSABLE_NEO4J") != "1")
                throw new InvalidOperationException("Only an explicitly disposable loopback Neo4j service is permitted.");
            var password = Environment.GetEnvironmentVariable("NEO4J_PASSWORD") ?? throw new InvalidOperationException("Missing test-service password.");
            var clock = new WorldClock { UtcNow = world.GetProperty("teachSteps")[0].GetProperty("atUtc").GetDateTimeOffset().AddDays(-1) };
            await using var host = new NativeHost(uri, password, clock, modules: true);
            await using var control = new NativeHost(uri, password, clock, modules: false);
            var runner = host.Services.GetRequiredService<INeo4jTransactionRunner>();
            if (_scenario == "upgrade-v2")
            {
                await Upgrade(host, runner, world, options["--upgrade-from"], options["--upgrade-spec"]);
                await Save(Checks.All(c => c.Passed) ? "upgrade_passed_acceptance_incomplete" : "failed");
                return Checks.All(c => c.Passed) ? 0 : 1;
            }
            if (CommitmentsModule.Manifest.Version.ToString() != "1.0.0")
                throw new InvalidOperationException("Primary and negative teaching require the v1 package.");
            var existing = await Count(runner, "MATCH (n) RETURN count(n) AS n");
            if (existing != 0) throw new InvalidOperationException("Primary world requires an empty disposable database; refusing existing data.");
            await host.Services.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();
            var migration = await new ModuleMigrator(runner, host.Resolution, clock).ApplyAsync("commitments-native-acceptance");
            Evidence.Add(new { kind = "migration", migration });
            foreach (var entity in world.GetProperty("entities").EnumerateArray())
                await runner.WriteAsync(async tx => await (await tx.RunAsync(
                    "CREATE (:Entity {id:$id,name:$name,owner_id:$owner,created_at:datetime($at)})",
                    new { id = Text(entity,"id"), name = Text(entity,"name"), owner = Text(entity,"owner"), at = clock.UtcNow.ToString("O") })).ConsumeAsync());
            // The fixture creates only core anchors. Module records must come through the real native write path.
            Require("no-seeded-module-records", await Count(runner, "MATCH (n:CommitmentItem) RETURN count(n) AS n") == 0);
            var controlQuery = world.GetProperty("queries").EnumerateArray().First(q => Text(q,"id") == "open-after-correction");
            // Prime the core host before comparing warm query shapes across retained module data.
            // Both measured reads still use fresh sessions; this warm-up is separately recorded.
            var warmup = await Query(control, controlQuery, "control-warmup");
            Evidence.Add(new { kind = "control-warmup", warmup });
            var baseline = await Query(control, controlQuery, "control-before");
            var captured = new Dictionary<string, CapturedIngestion>(StringComparer.Ordinal);
            var teachCount = 0; var queryCount = 0;
            foreach (var step in world.GetProperty("teachSteps").EnumerateArray())
            {
                var id = Text(step,"id");
                if (Text(step,"kind") == "nativeTeach")
                {
                    clock.UtcNow = step.GetProperty("atUtc").GetDateTimeOffset(); host.Selection = null;
                    var run = await host.RunAsync(Text(step,"owner"),Text(step,"session"),Text(step,"conversation"),
                        step.GetProperty("command").GetRawText(), Text(step,"userMessageId"),Text(step,"assistantMessageId"));
                    Evidence.Add(new { kind = "teach", id, run });
                    Require(id+"/one-persisted-ingestion", run.Ingestion.Count == 1);
                    if (run.Ingestion.Count == 1)
                    {
                        var receipt = run.Ingestion[0]; captured[id] = receipt;
                        CheckOutcome(id, step, receipt.Report);
                        var proposal = receipt.Report.Recorded.SelectMany(r => r.Proposals).SingleOrDefault();
                        Require(id+"/exact-user-provenance", proposal is not null && EqualIds(
                            proposal.Source.MessageIds, Strings(step,"expectedSourceIds")));
                        Require(id+"/native-source-identities", EqualIds(receipt.Request.Messages.Select(m=>m.Id),
                            new[] { "maf:"+Text(step,"userMessageId"), "maf:"+Text(step,"assistantMessageId") }));
                        var rows = await runner.ReadAsync(async tx => await (await tx.RunAsync(
                            "MATCH (n:CommitmentItem {id:$id}) RETURN toInteger(last(split(n.id,':'))) AS version,n.source_message_ids AS sources,n.owner_id AS owner",
                            new { id = Text(step,"expectedRecordId") })).ToListAsync());
                        Require(id+"/actual-stored-version", rows.Count == 1 && rows[0]["version"].As<long>() == step.GetProperty("expectedVersion").GetInt64());
                        Require(id+"/actual-stored-provenance", rows.Count == 1 && EqualIds(rows[0]["sources"].As<List<string>>(), Strings(step,"expectedSourceIds")));
                        Require(id+"/actual-stored-owner", rows.Count == 1 && rows[0]["owner"].As<string>() == Text(step,"owner"));
                    }
                }
                else if (Text(step,"kind") == "replayRecordedIngestion")
                {
                    var report = await host.ReplayAsync(captured[Text(step,"recordedStep")]);
                    Evidence.Add(new { kind = "replay", id, report }); CheckOutcome(id,step,report);
                    var key = captured[Text(step,"recordedStep")].Report.Recorded.Single().Proposals.Single().Key;
                    var count = await runner.ReadAsync(async tx => (await (await tx.RunAsync(
                        "MATCH (n:CommitmentItem {owner_id:$owner,key:$key}) RETURN count(n) AS n",
                        new { owner = captured[Text(step,"recordedStep")].Request.Owner, key })).SingleAsync())["n"].As<long>());
                    Require(id+"/version-count", count == step.GetProperty("expectedVersionCount").GetInt64());
                }
                else throw new InvalidOperationException("Unknown teaching step kind.");
                teachCount++;
                if (negative is { } selected)
                {
                    await Save("in_progress");
                    if (Text(selected, "forkAfter") == id)
                    {
                        await RunNegative(host, () => new NativeHost(uri, password, clock, modules: false), runner, world, selected, captured, warmup, baseline);
                        await Save(Checks.All(c => c.Passed) ? "negative_passed_acceptance_incomplete" : "failed");
                        return Checks.All(c => c.Passed) ? 0 : 1;
                    }
                    continue;
                }
                foreach (var query in world.GetProperty("queries").EnumerateArray().Where(q => Text(q,"after") == id))
                {
                    var previousTime = clock.UtcNow;
                    if (query.TryGetProperty("nowUtc",out var now)) clock.UtcNow = now.GetDateTimeOffset();
                    var run = await Query(host,query,Text(query,"id"));
                    Evidence.Add(new { kind = "query", id = Text(query,"id"), run });
                    CheckQuery(query,run); queryCount++;
                    if (Text(query,"id") == Text(controlQuery,"id"))
                    {
                        var disabled = await Query(control,query,"control-after");
                        Evidence.Add(new { kind = "module-off", baseline, disabled });
                        Require("module-off/no-module-execution", disabled.Envelopes.Count == 0 && disabled.Ingestion.Count == 0);
                        Require("module-off/no-teaching-history", disabled.Inputs.Count == 1 && !disabled.Inputs[0].AllText.Contains("agentmemory.commitment/v1",StringComparison.Ordinal));
                        Require("module-off/no-commitment-content", disabled.Inputs.Count == 1 && Strings(query,"requiredText").All(t=>!disabled.Inputs[0].AllText.Contains(t,StringComparison.Ordinal)));
                        Require("module-off/core-input-neutrality", SameInput(baseline,disabled));
                    }
                    clock.UtcNow = previousTime;
                }
                await Save("in_progress");
            }
            if (negative is not null) throw new InvalidOperationException("Negative case fork point was never reached.");
            Require("world/all-primary-teaching-steps",teachCount==world.GetProperty("teachSteps").GetArrayLength());
            Require("world/all-primary-queries",queryCount==world.GetProperty("queries").GetArrayLength());
            // Exercise the exact comparison used above: an omitted or substituted result must fail.
            var expected = Strings(controlQuery,"expectedIds");
            Require("runner-mutation/reject-missing-result",expected.Count>0 && !EqualIds([],expected));
            Require("runner-mutation/reject-substituted-result",!EqualIds(["mutated-record-id"],expected));
            Evidence.Add(new { kind = "retained-upgrade-snapshot", snapshot = await SnapshotCommitments(runner) });
            await Save(Checks.All(c=>c.Passed)?"primary_passed_acceptance_incomplete":"failed");
            return Checks.All(c=>c.Passed)?0:1;
        }
        catch (Exception error)
        {
            Evidence.Add(new { kind="fatal",type=error.GetType().FullName,error.Message,error.StackTrace });
            await Save("failed"); Console.Error.WriteLine(error); return 1;
        }
    }

    private static Task<NativeRun> Query(NativeHost host,JsonElement query,string runId)
    {
        host.Selection=query;
        return host.RunAsync(Text(query,"owner"),"fresh-"+runId,"conversation-"+runId,
            Question(query),runId+"-user",runId+"-assistant",
            query.TryGetProperty("systemAsOfUtc",out var system)?system.GetDateTimeOffset():null,
            query.TryGetProperty("validAsOfUtc",out var valid)?valid.GetDateTimeOffset():null);
    }
    private static void CheckQuery(JsonElement query,NativeRun run)
    {
        var id=Text(query,"id");
        Require(id+"/one-client-call",run.Inputs.Count==1);
        Require(id+"/one-compiled-envelope",run.Envelopes.Count==1);
        var items=run.Envelopes.SelectMany(e=>e.Sections).SelectMany(s=>s.Items).ToArray();
        Require(id+"/exact-record-identities",EqualIds(items.Select(i=>i.Id??""),Strings(query,"expectedIds")));
        var omissions=run.Envelopes.SelectMany(e=>e.Omissions).ToArray();
        if(query.TryGetProperty("expectedStatus",out var status))
            Require(id+"/expected-disposition",omissions.Any(o=>o.Reason.ToString()==status.GetString()));
        else Require(id+"/no-hidden-refusal",omissions.All(o=>o.Reason==ContextOmissionReason.SearchedAndEmpty));
        if(run.Inputs.Count!=1)return;
        var input=run.Inputs[0];
        Require(id+"/fresh-session-user-input",EqualIds(input.Messages.Where(m=>m.Role=="user").Select(m=>m.Text),[Question(query)]));
        Require(id+"/no-teaching-transcript",!input.AllText.Contains("agentmemory.commitment/v1",StringComparison.Ordinal));
        Require(id+"/records-reach-chat-client",items.All(i=>input.AllText.Contains(i.Text,StringComparison.Ordinal)));
        Require(id+"/required-text",Strings(query,"requiredText").All(t=>input.AllText.Contains(t,StringComparison.Ordinal)));
        Require(id+"/excluded-text",Strings(query,"excludedText").All(t=>!input.AllText.Contains(t,StringComparison.Ordinal)));
    }
    private static void CheckOutcome(string id,JsonElement step,IngestionReport report)
    {
        var outcomes=report.Providers.SelectMany(p=>p.Proposals).ToArray();
        Require(id+"/one-proposal",outcomes.Length==1);
        Require(id+"/expected-write-outcome",!report.Denied && report.Providers.All(p=>p.Status==ProviderIngestionStatus.Succeeded)
            && outcomes.Length==1 && outcomes[0].Status.ToString()==Text(step,"expectedOutcome"));
        Require(id+"/expected-record-id",outcomes.Length==1 && outcomes[0].RecordId==Text(step,"expectedRecordId"));
    }
    private static async Task<long> Count(INeo4jTransactionRunner runner,string query)
        =>await runner.ReadAsync(async tx=>(await (await tx.RunAsync(query)).SingleAsync())["n"].As<long>());
    private static bool SameInput(NativeRun a,NativeRun b)=>a.Inputs.Count==1 && b.Inputs.Count==1
        && a.Inputs[0].Instructions==b.Inputs[0].Instructions
        && a.Inputs[0].Messages.Select(m=>(m.Role,m.Text)).SequenceEqual(b.Inputs[0].Messages.Select(m=>(m.Role,m.Text)));
    private static bool EqualIds(IEnumerable<string> actual,IEnumerable<string> expected)=>actual.SequenceEqual(expected,StringComparer.Ordinal);
    private static string Text(JsonElement value,string name)=>value.GetProperty(name).GetString()??throw new FormatException("Null "+name);
    private static string Question(JsonElement q)=>q.TryGetProperty("question",out var question)?question.GetString()!:"What should I remember?";
    private static IReadOnlyList<string> Strings(JsonElement value,string name)=>value.TryGetProperty(name,out var array)?array.EnumerateArray().Select(e=>e.GetString()!).ToArray():[];
    private static void Require(string name,bool passed)=>Checks.Add(new(name,passed));
    private static async Task Save(string status)
    {
        var report=new
        {
            status,scenario=_scenario,createdAtUtc=DateTimeOffset.UtcNow,worldSha256=_worldHash,packageVersion=CommitmentsModule.Manifest.Version.ToString(),
            assembly=typeof(CommitmentsModule).Assembly.Location,sdk=typeof(ModuleReadContext).Assembly.Location,
            passed=Checks.Count(c=>c.Passed),failed=Checks.Count(c=>!c.Passed),checks=Checks,evidence=Evidence,
            limits=new[]{"Deterministic client captures model inputs; no inference or effectiveness score.",
                "This receipt covers only its named scenario. Other forks, v1/v2 migration and lifecycle require separate receipts.",
                "Static package activation; signed loading, draining and unload belong to the runtime acceptance task."}
        };
        await File.WriteAllTextAsync(Path.Combine(_output,"report.json"),JsonSerializer.Serialize(report,Json));
    }
    private sealed record Check(string Name,bool Passed);
}
