using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Cli.Perf;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// Opt-in M-18 concurrent-correctness and saturation characterization.
/// </summary>
public sealed class PerfConcurrencyCommand
{
    private const string Neo4jImage = "neo4j:5.26";
    private const string EntryEstimateSample = "neo4j.transaction_entry_ms_est";
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TextWriter _output;

    public PerfConcurrencyCommand(TextWriter output) => _output = output;

    public async Task<int> ExecuteAsync(
        string? label,
        string? levelsValue,
        string? poolSizeValue,
        string? dimensionsValue,
        string? outputRoot,
        CancellationToken cancellationToken = default)
    {
        var levels = ParseLevels(levelsValue);
        var poolSize = ParsePositive(poolSizeValue, 16, "pool-size");
        var dimensions = ParsePositive(dimensionsValue, 384, "embedding-dimensions");
        var runLabel = Sanitize(label) ?? "baseline";
        var startedAt = DateTimeOffset.UtcNow;
        var runId =
            $"{startedAt:yyyyMMdd'T'HHmmss'Z'}__{runLabel}__hermetic-concurrency-pool-{poolSize}";
        var runDirectory = Path.Combine(
            outputRoot ?? Path.Combine("artifacts", "perf-concurrency"),
            runId);
        Directory.CreateDirectory(runDirectory);

        var manifest = new
        {
            schemaVersion = 1,
            runId,
            profile = "hermetic-concurrency",
            startedAtUtc = startedAt,
            gitCommit = GitCommit(),
            runtime = Environment.Version.ToString(),
            os = Environment.OSVersion.ToString(),
            neo4jImage = Neo4jImage,
            embedding = "deterministic-fnv1a",
            embeddingDimensions = dimensions,
            connectionPoolSize = poolSize,
            concurrencyLevels = levels,
            workloads = new[]
            {
                "owner-isolation-read",
                "dedup-on-create-race",
                "owner-scoped-supersession",
            },
            transactionEntryDelayEstimate = new
            {
                name = EntryEstimateSample,
                unit = "milliseconds",
                exactPoolQueueWait = false,
                definition =
                    "memory.db.tx start to transaction callback entry; upper bound includes " +
                    "connection acquisition, routing, and transaction begin",
            },
            timingScope =
                "Hermetic local characterization only; timings are not deployment performance.",
        };

        await File.WriteAllTextAsync(
            Path.Combine(runDirectory, "run.json"),
            JsonSerializer.Serialize(manifest, Json),
            cancellationToken).ConfigureAwait(false);

        _output.WriteLine($"perf concurrency: run {runId}");
        using var trace = new TraceLogWriter(Path.Combine(runDirectory, "trace.ndjson"));
        trace.RunStart(runId, manifest);
        var runStopwatch = Stopwatch.StartNew();
        using var collector = new PerfCollector(trace);

        await using var profile = await HermeticProfile.StartAsync(
            dimensions,
            TimeSpan.Zero,
            TimeSpan.Zero,
            _output,
            PerfScale.Small,
            scriptedRules: null,
            cancellationToken,
            maxConnectionPoolSize: poolSize).ConfigureAwait(false);

        await WarmProductPoolAsync(profile, poolSize).ConfigureAwait(false);
        var longTerm = profile.Services.GetRequiredService<ILongTermMemoryService>();
        await SeedOwnerIsolationAsync(longTerm, levels.Max(), dimensions, cancellationToken)
            .ConfigureAwait(false);

        var levelOutcomes = new List<LevelOutcome>();
        foreach (var concurrency in levels)
        {
            _output.WriteLine($"perf concurrency: level {concurrency}");
            var owner = await MeasureWaveAsync(
                collector,
                $"PERF-C-{LevelId(concurrency)}/owner-isolation-read",
                concurrency,
                index => RunOwnerIsolationReadAsync(longTerm, index, cancellationToken))
                .ConfigureAwait(false);

            var dedupOwner = $"m18-dedup-owner-{concurrency}";
            var dedupSubject = $"m18 dedup subject {concurrency}";
            var dedupEmbedding = DeterministicEmbeddingGenerator.Vector(
                $"m18 dedup semantic equivalence {concurrency}", dimensions);
            var dedup = await MeasureWaveAsync(
                collector,
                $"PERF-C-{LevelId(concurrency)}/dedup-on-create-race",
                concurrency,
                index => RunDedupCreateAsync(
                    longTerm,
                    concurrency,
                    index,
                    dedupOwner,
                    dedupSubject,
                    dedupEmbedding,
                    cancellationToken))
                .ConfigureAwait(false);
            var dedupLiveFacts = await CountLiveDedupFactsAsync(
                profile.Driver, dedupOwner, dedupSubject).ConfigureAwait(false);

            var supersessionPrefix = $"m18-super-{concurrency}";
            await SeedSupersessionAsync(
                longTerm, concurrency, supersessionPrefix, dimensions, cancellationToken)
                .ConfigureAwait(false);
            var crossOwnerAttemptErrors = await RunCrossOwnerSupersessionProbesAsync(
                longTerm, concurrency, supersessionPrefix, cancellationToken).ConfigureAwait(false);
            var supersession = await MeasureWaveAsync(
                collector,
                $"PERF-C-{LevelId(concurrency)}/owner-scoped-supersession",
                concurrency,
                index => RunSupersessionAsync(
                    longTerm, concurrency, index, supersessionPrefix, cancellationToken))
                .ConfigureAwait(false);
            var supersessionShape = await InspectSupersessionAsync(
                profile.Driver, supersessionPrefix).ConfigureAwait(false);

            var allRecords = owner.Records.Concat(dedup.Records).Concat(supersession.Records).ToList();
            var entrySampleCount = allRecords.Sum(record =>
                record.Samples.TryGetValue(EntryEstimateSample, out var samples) ? samples.Count : 0);
            var correctness = new ConcurrencyCorrectnessSnapshot(
                concurrency,
                owner.FailedRequests + dedup.FailedRequests + supersession.FailedRequests +
                crossOwnerAttemptErrors,
                owner.OwnerLeaks,
                owner.OwnerMisses,
                dedupLiveFacts,
                supersessionShape.LosersPresent,
                supersessionShape.LosersClosed,
                supersessionShape.Edges,
                supersessionShape.WinnersLive,
                supersessionShape.CrossOwnerEdges,
                entrySampleCount);
            var issues = ConcurrencyRunValidator.Validate(correctness);
            levelOutcomes.Add(new LevelOutcome(
                concurrency,
                Analyze(owner),
                Analyze(dedup),
                Analyze(supersession),
                correctness,
                issues));
        }

        runStopwatch.Stop();
        var accepted = levelOutcomes.All(level => level.ValidationIssues.Count == 0);
        trace.RunEnd(collector.Records.Count, runStopwatch.Elapsed.TotalMilliseconds);

        var summary = new
        {
            schemaVersion = 1,
            runId,
            accepted,
            manifest,
            durationMilliseconds = runStopwatch.Elapsed.TotalMilliseconds,
            levels = levelOutcomes.Select(ToArtifact),
            validationIssues = levelOutcomes.SelectMany(level =>
                level.ValidationIssues.Select(issue => new
                {
                    concurrency = level.Concurrency,
                    code = issue,
                })),
        };
        await File.WriteAllTextAsync(
            Path.Combine(runDirectory, "summary.json"),
            JsonSerializer.Serialize(summary, Json),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(runDirectory, "report.md"),
            RenderReport(runId, poolSize, levelOutcomes, accepted),
            cancellationToken).ConfigureAwait(false);

        _output.WriteLine(
            accepted
                ? "perf concurrency: PASS"
                : "perf concurrency: FAIL");
        foreach (var level in levelOutcomes)
        {
            _output.WriteLine(
                $"  c={level.Concurrency}: errors={level.Correctness.OperationErrors}, " +
                $"leaks={level.Correctness.OwnerLeaks}, misses={level.Correctness.OwnerMisses}, " +
                $"dedup-live={level.Correctness.DedupLiveFacts}, " +
                $"supersession={level.Correctness.SupersessionEdges}/{level.Concurrency}, " +
                $"cross-owner-edges={level.Correctness.CrossOwnerEdges}");
            foreach (var issue in level.ValidationIssues)
                _output.WriteLine($"  error: c={level.Concurrency} {issue}");
        }

        _output.WriteLine($"perf concurrency: wrote {runDirectory}");
        return accepted ? 0 : 1;
    }

    private static async Task SeedOwnerIsolationAsync(
        ILongTermMemoryService longTerm,
        int count,
        int dimensions,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < count; index++)
        {
            await longTerm.AddFactAsync(new Fact
            {
                FactId = $"m18-owner-fact-{index}",
                Subject = "m18 shared owner-isolation subject",
                Predicate = "belongs_to",
                Object = $"owner-marker-{index}",
                OwnerId = Owner(index),
                Confidence = 0.9,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Embedding = DeterministicEmbeddingGenerator.Vector(
                    $"m18 owner isolation marker {index}", dimensions),
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<RequestCorrectness> RunOwnerIsolationReadAsync(
        ILongTermMemoryService longTerm,
        int index,
        CancellationToken cancellationToken)
    {
        var facts = await longTerm.GetFactsBySubjectAsync(
            "m18 shared owner-isolation subject",
            MemoryScope.For(Owner(index)),
            cancellationToken).ConfigureAwait(false);
        var leaks = facts.Count(fact =>
            !string.Equals(fact.OwnerId, Owner(index), StringComparison.Ordinal));
        var own = facts.Count(fact =>
            string.Equals(fact.OwnerId, Owner(index), StringComparison.Ordinal) &&
            string.Equals(fact.Object, $"owner-marker-{index}", StringComparison.Ordinal));
        return new RequestCorrectness(leaks, own == 1 ? 0 : 1);
    }

    private static async Task<RequestCorrectness> RunDedupCreateAsync(
        ILongTermMemoryService longTerm,
        int concurrency,
        int index,
        string owner,
        string subject,
        float[] embedding,
        CancellationToken cancellationToken)
    {
        await longTerm.AddFactAsync(new Fact
        {
            FactId = $"m18-dedup-{concurrency}-{index}",
            Subject = subject,
            Predicate = "semantically_equivalent_to",
            Object = $"wording-{index}",
            OwnerId = owner,
            Confidence = 0.8,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Embedding = embedding,
        }, cancellationToken).ConfigureAwait(false);
        return RequestCorrectness.Clean;
    }

    private static async Task SeedSupersessionAsync(
        ILongTermMemoryService longTerm,
        int concurrency,
        string prefix,
        int dimensions,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < concurrency; index++)
        {
            var owner = $"{prefix}-owner-{index}";
            await longTerm.AddFactAsync(new Fact
            {
                FactId = $"{prefix}-loser-{index}",
                Subject = $"{prefix}-subject-{index}",
                Predicate = "status_old",
                Object = "old",
                OwnerId = owner,
                Confidence = 0.7,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                Embedding = DeterministicEmbeddingGenerator.Vector(
                    $"{prefix} old {index}", dimensions),
            }, cancellationToken).ConfigureAwait(false);
            await longTerm.AddFactAsync(new Fact
            {
                FactId = $"{prefix}-winner-{index}",
                Subject = $"{prefix}-subject-{index}",
                Predicate = "status_new",
                Object = "new",
                OwnerId = owner,
                Confidence = 0.95,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Embedding = DeterministicEmbeddingGenerator.Vector(
                    $"{prefix} new {index}", dimensions),
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunCrossOwnerSupersessionProbesAsync(
        ILongTermMemoryService longTerm,
        int concurrency,
        string prefix,
        CancellationToken cancellationToken)
    {
        if (concurrency < 2) return 0;
        var probes = Enumerable.Range(0, concurrency).Select(async index =>
        {
            try
            {
                var foreignWinner = (index + 1) % concurrency;
                var changed = await longTerm.SupersedeFactAsync(
                    $"{prefix}-loser-{index}",
                    $"{prefix}-winner-{foreignWinner}",
                    MemoryScope.For($"{prefix}-owner-{index}"),
                    cancellationToken).ConfigureAwait(false);
                return changed ? 1 : 0;
            }
            catch
            {
                return 1;
            }
        });
        return (await Task.WhenAll(probes).ConfigureAwait(false)).Sum();
    }

    private static async Task<RequestCorrectness> RunSupersessionAsync(
        ILongTermMemoryService longTerm,
        int concurrency,
        int index,
        string prefix,
        CancellationToken cancellationToken)
    {
        _ = concurrency;
        var changed = await longTerm.SupersedeFactAsync(
            $"{prefix}-loser-{index}",
            $"{prefix}-winner-{index}",
            MemoryScope.For($"{prefix}-owner-{index}"),
            cancellationToken).ConfigureAwait(false);
        if (!changed)
            throw new InvalidOperationException("Owner-scoped supersession matched no pair.");
        return RequestCorrectness.Clean;
    }

    private static async Task<WaveOutcome> MeasureWaveAsync(
        PerfCollector collector,
        string scenario,
        int concurrency,
        Func<int, Task<RequestCorrectness>> operation)
    {
        var ready = new CountdownEvent(concurrency);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomes = new ConcurrentBag<MeasuredRequest>();
        var tasks = Enumerable.Range(0, concurrency).Select(async index =>
        {
            ready.Signal();
            await release.Task.ConfigureAwait(false);

            TurnRecord record;
            var failed = false;
            var correctness = RequestCorrectness.Clean;
            using (var turn = collector.BeginTurn(scenario, index, "measure"))
            {
                record = turn.Record;
                try
                {
                    correctness = await operation(index).ConfigureAwait(false);
                }
                catch
                {
                    failed = true;
                }
            }

            outcomes.Add(new MeasuredRequest(record, failed, correctness));
        }).ToArray();

        if (!ready.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException($"Concurrency wave '{scenario}' did not become ready.");
        var stopwatch = Stopwatch.StartNew();
        release.SetResult();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        stopwatch.Stop();

        var ordered = outcomes.OrderBy(outcome => outcome.Record.Iteration).ToList();
        return new WaveOutcome(
            scenario,
            concurrency,
            stopwatch.Elapsed.TotalMilliseconds,
            ordered.Select(outcome => outcome.Record).ToList(),
            ordered.Count(outcome => outcome.Failed),
            ordered.Sum(outcome => outcome.Correctness.OwnerLeaks),
            ordered.Sum(outcome => outcome.Correctness.OwnerMisses));
    }

    private static ConcurrencyLevelAnalysis Analyze(WaveOutcome wave)
    {
        var entrySamples = wave.Records.SelectMany(record =>
            record.Samples.TryGetValue(EntryEstimateSample, out var values)
                ? values
                : Array.Empty<double>()).ToList();
        return ConcurrencyAnalysis.Analyze(
            wave.Concurrency,
            wave.ElapsedMilliseconds,
            wave.Records.Select(record => record.DurationMs).ToList(),
            entrySamples,
            wave.FailedRequests);
    }

    private static async Task WarmProductPoolAsync(HermeticProfile profile, int poolSize)
    {
        var driver = profile.Services.GetRequiredService<IDriver>();
        var tasks = Enumerable.Range(0, poolSize).Select(async poolIndex =>
        {
            _ = poolIndex;
            await using var session = driver.AsyncSession(config =>
                config.WithDatabase("neo4j").WithDefaultAccessMode(AccessMode.Read));
            var cursor = await session.RunAsync("RETURN 1 AS warmed").ConfigureAwait(false);
            _ = await cursor.SingleAsync().ConfigureAwait(false);
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<long> CountLiveDedupFactsAsync(
        IDriver driver,
        string owner,
        string subject)
    {
        const string cypher = """
            MATCH (f:Fact {owner_id: $owner, subject: $subject, predicate: 'semantically_equivalent_to'})
            WHERE f.invalidated_at IS NULL
            RETURN count(f) AS count
            """;
        await using var session = driver.AsyncSession(config => config.WithDatabase("neo4j"));
        var cursor = await session.RunAsync(cypher, new { owner, subject }).ConfigureAwait(false);
        return (await cursor.SingleAsync().ConfigureAwait(false))["count"].As<long>();
    }

    private static async Task<SupersessionShape> InspectSupersessionAsync(
        IDriver driver,
        string prefix)
    {
        const string cypher = """
            CALL {
                MATCH (loser:Fact)
                WHERE loser.id STARTS WITH $loserPrefix
                RETURN count(loser) AS losersPresent,
                       count(CASE WHEN loser.invalidated_at IS NOT NULL
                                       AND loser.valid_until IS NOT NULL THEN 1 END) AS losersClosed
            }
            CALL {
                MATCH (winner:Fact)
                WHERE winner.id STARTS WITH $winnerPrefix
                  AND winner.invalidated_at IS NULL
                  AND winner.valid_until IS NULL
                RETURN count(winner) AS winnersLive
            }
            CALL {
                MATCH (loser:Fact)-[:SUPERSEDED_BY]->(winner:Fact)
                WHERE loser.id STARTS WITH $loserPrefix
                RETURN count(*) AS edges,
                       count(CASE WHEN loser.owner_id <> winner.owner_id THEN 1 END) AS crossOwnerEdges
            }
            RETURN losersPresent, losersClosed, winnersLive, edges, crossOwnerEdges
            """;
        await using var session = driver.AsyncSession(config => config.WithDatabase("neo4j"));
        var cursor = await session.RunAsync(
            cypher,
            new
            {
                loserPrefix = $"{prefix}-loser-",
                winnerPrefix = $"{prefix}-winner-",
            }).ConfigureAwait(false);
        var record = await cursor.SingleAsync().ConfigureAwait(false);
        return new SupersessionShape(
            record["losersPresent"].As<long>(),
            record["losersClosed"].As<long>(),
            record["winnersLive"].As<long>(),
            record["edges"].As<long>(),
            record["crossOwnerEdges"].As<long>());
    }

    private static object ToArtifact(LevelOutcome level) => new
    {
        concurrency = level.Concurrency,
        ownerIsolationRead = level.OwnerIsolationRead,
        dedupOnCreateRace = level.DedupOnCreateRace,
        ownerScopedSupersession = level.OwnerScopedSupersession,
        correctness = new
        {
            operationErrors = level.Correctness.OperationErrors,
            ownerLeaks = level.Correctness.OwnerLeaks,
            ownerMisses = level.Correctness.OwnerMisses,
            dedupLiveFacts = level.Correctness.DedupLiveFacts,
            supersessionLosersPresent = level.Correctness.SupersessionLosersPresent,
            supersessionLosersClosed = level.Correctness.SupersessionLosersClosed,
            supersessionEdges = level.Correctness.SupersessionEdges,
            supersessionWinnersLive = level.Correctness.SupersessionWinnersLive,
            crossOwnerEdges = level.Correctness.CrossOwnerEdges,
            transactionEntryEstimateSamples =
                level.Correctness.TransactionEntryEstimateSamples,
        },
        validationIssues = level.ValidationIssues,
    };

    private static string RenderReport(
        string runId,
        int poolSize,
        IReadOnlyList<LevelOutcome> levels,
        bool accepted)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# Concurrency characterization — `{runId}`");
        builder.AppendLine();
        builder.AppendLine(accepted ? "**PASS ✅**" : "**FAIL ❌**");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Fixed product-driver pool: **{poolSize} connections**.");
        builder.AppendLine(
            "`transaction_entry_ms_est` is an upper-bound estimate from transaction-span start to " +
            "the first query callback. It includes acquisition, routing, and transaction begin; it is " +
            "not exact pool queue time. All timings are local hermetic characterization, not deployment performance.");
        builder.AppendLine();
        builder.AppendLine(
            "| Workload | Sessions | req p50 ms | req p95 ms | req p99 ms | entry-est p99 ms | ops/s | errors |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var level in levels)
        {
            AppendWorkload(builder, "owner isolation", level.OwnerIsolationRead);
            AppendWorkload(builder, "dedup race", level.DedupOnCreateRace);
            AppendWorkload(builder, "supersession", level.OwnerScopedSupersession);
        }

        builder.AppendLine();
        builder.AppendLine("| Sessions | leaks | misses | dedup live | losers present/closed | edges | winners live | cross-owner edges |");
        builder.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var level in levels)
        {
            var c = level.Correctness;
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {level.Concurrency} | {c.OwnerLeaks} | {c.OwnerMisses} | {c.DedupLiveFacts} | " +
                $"{c.SupersessionLosersPresent}/{c.SupersessionLosersClosed} | " +
                $"{c.SupersessionEdges} | {c.SupersessionWinnersLive} | {c.CrossOwnerEdges} |");
        }

        if (!accepted)
        {
            builder.AppendLine();
            builder.AppendLine("## Validation failures");
            foreach (var level in levels)
            foreach (var issue in level.ValidationIssues)
                builder.AppendLine(CultureInfo.InvariantCulture, $"- c={level.Concurrency}: `{issue}`");
        }

        return builder.ToString();
    }

    private static void AppendWorkload(
        StringBuilder builder,
        string workload,
        ConcurrencyLevelAnalysis result)
    {
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"| {workload} | {result.Concurrency} | {result.RequestMilliseconds.P50:F3} | " +
            $"{result.RequestMilliseconds.P95:F3} | {result.RequestMilliseconds.P99:F3} | " +
            $"{result.TransactionEntryDelayEstimateMilliseconds.P99:F3} | " +
            $"{result.AchievedOperationsPerSecond:F2} | {result.ErrorRate:P2} |");
    }

    private static IReadOnlyList<int> ParseLevels(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "1,10,100" : value;
        var parsed = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => ParsePositive(token, 0, "levels"))
            .Distinct()
            .Order()
            .ToArray();
        if (parsed.Length == 0)
            throw new ArgumentException("--levels must contain at least one positive integer.");
        return parsed;
    }

    private static int ParsePositive(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (fallback > 0) return fallback;
            throw new ArgumentException($"--{name} must be a positive integer.");
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed <= 0)
            throw new ArgumentException($"--{name} must be a positive integer.");
        return parsed;
    }

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sanitized = new string(value.Trim().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }

    private static string? GitCommit()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5_000);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Owner(int index) => $"m18-owner-{index}";

    private static string LevelId(int concurrency) => concurrency switch
    {
        1 => "01",
        10 => "02",
        100 => "03",
        _ => $"X{concurrency}",
    };

    private sealed record RequestCorrectness(int OwnerLeaks, int OwnerMisses)
    {
        internal static RequestCorrectness Clean { get; } = new(0, 0);
    }

    private sealed record MeasuredRequest(
        TurnRecord Record,
        bool Failed,
        RequestCorrectness Correctness);

    private sealed record WaveOutcome(
        string Workload,
        int Concurrency,
        double ElapsedMilliseconds,
        IReadOnlyList<TurnRecord> Records,
        int FailedRequests,
        int OwnerLeaks,
        int OwnerMisses);

    private sealed record SupersessionShape(
        long LosersPresent,
        long LosersClosed,
        long WinnersLive,
        long Edges,
        long CrossOwnerEdges);

    private sealed record LevelOutcome(
        int Concurrency,
        ConcurrencyLevelAnalysis OwnerIsolationRead,
        ConcurrencyLevelAnalysis DedupOnCreateRace,
        ConcurrencyLevelAnalysis OwnerScopedSupersession,
        ConcurrencyCorrectnessSnapshot Correctness,
        IReadOnlyList<string> ValidationIssues);
}
