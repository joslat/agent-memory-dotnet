using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.Gate;
using AgentMemory.Inference;

namespace AgentMemory.RouterArena.Ask;

internal static class CompoundEvaluationCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Dataset
    {
        public required int SchemaVersion { get; init; }
        public required string Status { get; init; }
        public required DateTimeOffset Now { get; init; }
        public required IReadOnlyList<CompoundCase> Cases { get; init; }
    }

    public static async Task<int> RunAsync(string datasetPath, string configurationPath, string outputPrefix,
        bool dryRun, int? limit, TextWriter output, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] datasetBytes, configurationBytes;
        Dataset dataset;
        MemoryGateOptions options;
        try
        {
            datasetBytes = await File.ReadAllBytesAsync(datasetPath, cancellationToken).ConfigureAwait(false);
            configurationBytes = await File.ReadAllBytesAsync(configurationPath, cancellationToken).ConfigureAwait(false);
            dataset = JsonSerializer.Deserialize<Dataset>(datasetBytes, Json) ?? throw new FormatException("Missing dataset.");
            options = JsonSerializer.Deserialize<MemoryGateOptions>(configurationBytes,
                new JsonSerializerOptions(Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                ?? throw new FormatException("Missing configuration.");
            Validate(dataset, options, limit);
            if (new[] { ".baseline.json", ".compound.json", ".baseline.json.tmp", ".compound.json.tmp", ".lock" }
                .Any(suffix => File.Exists(outputPrefix + suffix)))
                throw new FormatException("Output evidence already exists; choose a new output prefix.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        {
            output.WriteLine($"compound-evaluate: invalid input or existing output ({ex.GetType().Name}). Nothing was sent.");
            return 1;
        }

        var selected = dataset.Cases.Take(limit ?? dataset.Cases.Count).ToList();
        var judgeCount = SystemOneMemoryGate.UsableJudges(options).Count();
        var baselineRequests = selected.Sum(c => c.Candidates.Select(m => m.MemoryType).Distinct(StringComparer.Ordinal).Count()) * judgeCount;
        output.WriteLine($"compound-evaluate: {selected.Count} cases; {baselineRequests} baseline HTTP requests before retries; "
            + "paired compound arm adds at most three gate decisions, one detector decision and one splitter call per case.");
        if (dryRun)
        {
            output.WriteLine("Dry run validated inputs and output paths. Nothing was sent; no credentials or clients were resolved.");
            return 0;
        }

        // Operator experiment policy: no provider auto-detection and no accidental use of OPENAI_API_KEY.
        if (!string.Equals(Environment.GetEnvironmentVariable("AI_INFERENCE_PROVIDER"), "bitdeer", StringComparison.OrdinalIgnoreCase) ||
            InferenceProviderEnvironment.Resolve().Settings is not { Provider: InferenceProvider.Bitdeer } settings)
        {
            output.WriteLine("compound-evaluate: explicitly configure AI_INFERENCE_PROVIDER=bitdeer and its credentials.");
            return 1;
        }

        var baselineRows = new List<CompoundEvaluationRow>();
        var compoundRows = new List<CompoundEvaluationRow>();
        var datasetHash = Hash(datasetBytes);
        var configurationHash = Hash(configurationBytes);
        var started = DateTimeOffset.UtcNow;
        var gateHash = Hash(await File.ReadAllBytesAsync(typeof(MemoryGateOptions).Assembly.Location, cancellationToken).ConfigureAwait(false));
        var runnerHash = Hash(await File.ReadAllBytesAsync(typeof(CompoundEvaluationCommand).Assembly.Location, cancellationToken).ConfigureAwait(false));
        var model = settings.EffectiveExtractionModel;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPrefix))!);
        FileStream reservation;
        try { reservation = new FileStream(outputPrefix + ".lock", FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException)
        {
            output.WriteLine("compound-evaluate: output prefix is already reserved. Nothing was sent.");
            return 1;
        }
        using var ownedReservation = reservation;
        var initialized = false;

        async Task SaveAsync(bool completed, string? stopReason = null)
        {
            foreach (var (arm, rows) in new[] { ("baseline", baselineRows), ("compound", compoundRows) })
            {
                var path = outputPrefix + "." + arm + ".json";
                var payload = new
                {
                    schemaVersion = 1, datasetSha256 = datasetHash, configurationSha256 = configurationHash,
                    datasetStatus = dataset.Status, gateAssemblySha256 = gateHash, runnerAssemblySha256 = runnerHash,
                    arm, pairedBaseline = true, startedAtUtc = started, completed, stopReason,
                    expectedCases = dataset.Cases.Count, selectedCases = selected.Count,
                    provider = "bitdeer", chatModel = model, chatEndpoint = settings.SafeEndpoint,
                    chatEndpointSha256 = Hash(Encoding.UTF8.GetBytes(settings.Endpoint)),
                    chatSdkRetries = 0, freshClientsPerCase = true, timeoutMs = options.Timeout.TotalMilliseconds,
                    threshold = options.Threshold, detectorThreshold = 0.5,
                    scope = "Fixed-pool gate selection. Baseline failure is a failed case; no store or similarity-floor simulation.",
                    usage = "Only complete provider-reported token pairs are summed. Missing reports are unknown; totals may be partial.",
                    rows,
                };
                var json = JsonSerializer.Serialize(payload, Json);
                if (!initialized)
                {
                    await using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await created.WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await File.WriteAllTextAsync(path + ".tmp", json, cancellationToken).ConfigureAwait(false);
                    File.Move(path + ".tmp", path, overwrite: true);
                }
            }
            initialized = true;
        }

        try { await SaveAsync(completed: false).ConfigureAwait(false); }
        catch (IOException)
        {
            output.WriteLine("compound-evaluate: could not create new evidence files. Nothing was sent; use a new output prefix.");
            return 1;
        }
        var consecutiveFailures = 0;
        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var session = new CompoundEvaluationSession(options, settings.Endpoint, settings.ApiKey, model);
            var pair = await CompoundEvaluation.RunCaseAsync(item, dataset.Now, options, session.Gate, session.Planner,
                session.Snapshot, cancellationToken).ConfigureAwait(false);
            baselineRows.Add(pair.Baseline);
            compoundRows.Add(pair.Compound);
            consecutiveFailures = pair.Baseline.Error is null ? 0 : consecutiveFailures + 1;
            await SaveAsync(completed: false).ConfigureAwait(false);
            output.WriteLine($"case {baselineRows.Count}/{selected.Count}: baseline {pair.Baseline.Kept.Count}, "
                + $"compound {pair.Compound.Kept.Count}; {pair.Compound.Calls} HTTP attempts; "
                + $"{pair.Compound.Error ?? pair.Compound.Compound?.Outcome ?? "empty"}");
            if (consecutiveFailures >= 3)
            {
                await SaveAsync(completed: false, stopReason: "three-consecutive-baseline-failures").ConfigureAwait(false);
                return 2;
            }
        }
        await SaveAsync(completed: true).ConfigureAwait(false);
        return compoundRows.Any(r => r.Error is not null || r.PendingRequests != 0) ? 2 : 0;
    }

    private static void Validate(Dataset dataset, MemoryGateOptions options, int? limit)
    {
        if (dataset.SchemaVersion != 1 || string.IsNullOrWhiteSpace(dataset.Status) || dataset.Cases is not { Count: > 0 } ||
            limit is <= 0 || dataset.Cases.Any(c => c is null) ||
            dataset.Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != dataset.Cases.Count)
            throw new FormatException("Invalid dataset header or duplicate case IDs.");
        foreach (var item in dataset.Cases)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Question) || item.Candidates is null || item.Conversation is null ||
                item.Candidates.Any(c => c is null || string.IsNullOrWhiteSpace(c.Key) || string.IsNullOrWhiteSpace(c.MemoryType) || string.IsNullOrWhiteSpace(c.Text)) ||
                item.Conversation.Any(t => t is null || string.IsNullOrWhiteSpace(t.Role) || t.Text is null) ||
                item.Candidates.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != item.Candidates.Count)
                throw new FormatException("Invalid question or candidate pool.");
        }
        if (options.Mode != MemoryGateMode.Judge || options.Threshold is not (>= 0 and <= 1) ||
            options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1) || options.ExamplesPath is not null ||
            options.JudgeFailuresBeforeCooldown < 0 || options.JudgeCooldown <= TimeSpan.Zero ||
            options.Judges is not { Count: > 0 } || !ValidEndpoint(options.CompoundDetector) ||
            options.Judges.Any(j => !ValidEndpoint(j) || !double.IsFinite(j.Weight) || j.Weight <= 0))
            throw new FormatException("Invalid gate configuration or unhashed example input.");
    }

    private static bool ValidEndpoint(SystemOneEndpoint? endpoint) => endpoint is not null &&
        !string.IsNullOrWhiteSpace(endpoint.Name) && !string.IsNullOrWhiteSpace(endpoint.Model) &&
        endpoint.Endpoint is { IsAbsoluteUri: true } uri && uri.Scheme is "http" or "https";

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
