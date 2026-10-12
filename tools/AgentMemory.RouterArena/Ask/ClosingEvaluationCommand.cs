using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.Gate;

namespace AgentMemory.RouterArena.Ask;

internal static class ClosingEvaluationCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<int> RunAsync(string datasetPath, string configurationPath, string outputPrefix,
        bool dryRun, int? limit, TextWriter output, CancellationToken cancellationToken = default,
        Func<HttpMessageHandler>? transportFactory = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] datasetBytes, configurationBytes;
        ClosingDataset dataset;
        ClosingConfiguration configuration;
        ClosingEvaluationPlan plan;
        try
        {
            datasetBytes = await File.ReadAllBytesAsync(datasetPath, cancellationToken).ConfigureAwait(false);
            configurationBytes = await File.ReadAllBytesAsync(configurationPath, cancellationToken).ConfigureAwait(false);
            dataset = JsonSerializer.Deserialize<ClosingDataset>(datasetBytes, Json) ?? throw new FormatException();
            configuration = JsonSerializer.Deserialize<ClosingConfiguration>(configurationBytes, Json) ?? throw new FormatException();
            plan = ClosingEvaluationPlan.Build(dataset, configuration, limit);
            if (new[] { ".json", ".json.tmp", ".lock" }.Any(suffix => File.Exists(outputPrefix + suffix)))
                throw new FormatException("Evidence exists.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        {
            output.WriteLine($"closing-evaluate: invalid input or existing output ({ex.GetType().Name}). Nothing was sent.");
            return 1;
        }
        output.WriteLine($"closing-evaluate: {plan.Cases.Count} source rows, {plan.Cases.Select(c => c.ProposalId).Distinct().Count()} distinct proposals, "
            + $"{plan.Requests.Count} logical requests, at most {plan.Requests.Count * 2} HTTP attempts with library retries.");
        if (dryRun)
        {
            output.WriteLine("Dry run validated inputs. Nothing was sent; no credentials, clients or output files were created.");
            return 0;
        }
        if (configuration.Judge.KeyVariable is { } variable && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
        {
            output.WriteLine("closing-evaluate: the explicitly configured credential is missing. Nothing was sent.");
            return 1;
        }

        var datasetHash = ClosingEvaluationPlan.Hash(datasetBytes);
        var configHash = ClosingEvaluationPlan.Hash(configurationBytes);
        var gateHash = ClosingEvaluationPlan.Hash(await File.ReadAllBytesAsync(typeof(SystemOneUpdateJudge).Assembly.Location, cancellationToken).ConfigureAwait(false));
        var runnerHash = ClosingEvaluationPlan.Hash(await File.ReadAllBytesAsync(typeof(ClosingEvaluationCommand).Assembly.Location, cancellationToken).ConfigureAwait(false));
        string CriteriaHash(bool named)
        {
            var criteria = named ? SystemOneUpdateJudge.NamedCriteria : SystemOneUpdateJudge.Criteria;
            return ClosingEvaluationPlan.Hash(JsonSerializer.SerializeToUtf8Bytes(new { criteria.True, criteria.False }, Json));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPrefix))!);
        FileStream reservation;
        try { reservation = new FileStream(outputPrefix + ".lock", FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException) { output.WriteLine("closing-evaluate: output prefix reserved. Nothing was sent."); return 1; }
        using var ownedReservation = reservation;
        var responses = new List<ClosingResponse>();
        var started = DateTimeOffset.UtcNow;
        var initialized = false;
        async Task SaveAsync(bool completed, string? stopReason = null)
        {
            var payload = new
            {
                schemaVersion = 1, datasetSha256 = datasetHash, configurationSha256 = configHash,
                datasetPurpose = dataset.Purpose, gateAssemblySha256 = gateHash, runnerAssemblySha256 = runnerHash,
                namedCriteriaSha256 = CriteriaHash(true), similarityCriteriaSha256 = CriteriaHash(false),
                model = configuration.Judge.Model, endpoint = configuration.Judge.Endpoint!.AbsoluteUri,
                timeoutMs = configuration.Timeout.TotalMilliseconds, freshClientPerRequest = true,
                startedAtUtc = started, completed, stopReason, expectedCases = dataset.Cases.Count,
                selectedCases = plan.Cases.Count, baselineNamedThreshold = .60, baselineSimilarityThreshold = .65,
                candidateThresholds = new[] { .55, .60, .65 }, expectedRequestIds = plan.Requests.Select(r => r.Id),
                scope = "Fixed-pair development comparison; correlated source rows share physical request results. No store or answer-quality measurement.",
                usage = "Provider-reported complete token pairs only; missing usage is unknown, not zero.",
                cases = plan.Cases, responses,
            };
            var path = outputPrefix + ".json";
            var target = initialized ? path + ".tmp" : path;
            // Cancellation still checkpoints completed responses. The prefix lock owns these paths.
            await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await file.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(payload, Json), CancellationToken.None).ConfigureAwait(false);
            if (initialized) File.Move(target, path, overwrite: true);
            initialized = true;
        }
        try { await SaveAsync(false).ConfigureAwait(false); }
        catch (IOException) { output.WriteLine("closing-evaluate: evidence creation failed. Nothing was sent."); return 1; }
        var consecutiveFailures = 0;
        try
        {
            foreach (var request in plan.Requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await ClosingEvaluationSession.RunAsync(request, configuration, transportFactory?.Invoke(), cancellationToken).ConfigureAwait(false);
                responses.Add(response);
                consecutiveFailures = response.Error is null ? 0 : consecutiveFailures + 1;
                await SaveAsync(false).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                output.WriteLine($"request {responses.Count}/{plan.Requests.Count}: {response.Error ?? "answered"}; {response.Counts.Attempts} HTTP attempts");
                if (consecutiveFailures >= 3 || response.Counts.Pending != 0)
                {
                    await SaveAsync(false, response.Counts.Pending != 0 ? "pending-request" : "three-consecutive-failures").ConfigureAwait(false);
                    return 2;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await SaveAsync(false, "cancelled").ConfigureAwait(false);
            throw;
        }
        await SaveAsync(true).ConfigureAwait(false);
        return responses.Any(r => r.Error is not null) ? 2 : 0;
    }
}
