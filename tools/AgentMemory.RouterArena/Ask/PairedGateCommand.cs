using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;
using AgentMemory.RouterArena.Data;
using Microsoft.Extensions.Options;

namespace AgentMemory.RouterArena.Ask;

internal static class PairedGateCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed record Input
    {
        public required int SchemaVersion { get; init; }
        public required string Role { get; init; }
        public required int MaxContextCharacters { get; init; }
        public required MemoryGateRequest Baseline { get; init; }
        public required MemoryGateRequest Overlay { get; init; }
    }

    public static async Task<int> RunAsync(string inputPath, string configurationPath, string outputDirectory,
        bool execute, TextWriter output, CancellationToken cancellationToken = default,
        Func<HttpMessageHandler>? transportFactory = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] inputBytes, configurationBytes;
        Input input;
        MemoryGateOptions options;
        try
        {
            inputBytes = await BoundedJson.ReadAsync(inputPath, cancellationToken).ConfigureAwait(false);
            configurationBytes = await BoundedJson.ReadAsync(configurationPath, cancellationToken).ConfigureAwait(false);
            input = JsonSerializer.Deserialize<Input>(inputBytes, Json) ?? throw new FormatException();
            options = JsonSerializer.Deserialize<MemoryGateOptions>(configurationBytes, Json) ?? throw new FormatException();
            if (input.SchemaVersion != 1 || input.Role is not ("development" or "synthetic-development") ||
                options.Mode != MemoryGateMode.Judge || options.ExamplesPath is not null || options.CompoundQuestions || options.UpdateJudge ||
                options.Judges is not { Count: > 0 and <= 4 } ||
                options.Judges.Select(j => j?.Name).Distinct(StringComparer.Ordinal).Count() != options.Judges.Count ||
                options.Judges.Any(j => !ValidJudge(j, input.Role)) || options.JudgeFailuresBeforeCooldown < 0 || options.JudgeCooldown <= TimeSpan.Zero)
                throw new FormatException("Invalid development gate configuration.");
            PairedGateEvaluation.ValidatePair(input.Baseline, input.Overlay, options.Threshold, input.MaxContextCharacters, options.Timeout);
            if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory)) throw new FormatException("Output exists.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        {
            output.WriteLine($"paired-gate: invalid input or existing output ({ex.GetType().Name}). Nothing sent.");
            return 1;
        }
        output.WriteLine("paired-gate: controlled gate/context comparison over recorded candidate pools; no search or answer-quality measurement.");
        if (!execute)
        {
            output.WriteLine("Preparation validated; no clients, credentials or output files created. Use --execute to dispatch.");
            return 0;
        }
        Directory.CreateDirectory(outputDirectory);
        using var reservation = new FileStream(Path.Combine(outputDirectory, ".lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        WriteNew(Path.Combine(outputDirectory, "input.json"), inputBytes);
        WriteNew(Path.Combine(outputDirectory, "configuration.json"), configurationBytes);
        using var journal = new FileStream(Path.Combine(outputDirectory, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var sync = new object();
        var closed = false;
        var failed = false;
        var sequence = 0;
        void Emit(object value)
        {
            lock (sync)
            {
                // A non-cooperative timed-out transport cannot append into a later run. Its start remains unknown.
                if (closed) return;
                if (failed) throw new IOException("Evidence journal failed before this dispatch.");
                try
                {
                    journal.Write(JsonSerializer.SerializeToUtf8Bytes(value, Json));
                    journal.WriteByte((byte)'\n');
                    journal.Flush(flushToDisk: true);
                }
                catch { failed = true; throw; }
            }
        }
        var manifest = new
        {
            schemaVersion = 1, role = input.Role, inputSha256 = Hash(inputBytes), configurationSha256 = Hash(configurationBytes),
            gateAssemblySha256 = Hash(await File.ReadAllBytesAsync(typeof(MemoryGateOptions).Assembly.Location, cancellationToken).ConfigureAwait(false)),
            runnerAssemblySha256 = Hash(await File.ReadAllBytesAsync(typeof(PairedGateCommand).Assembly.Location, cancellationToken).ConfigureAwait(false)),
            startedAtUtc = DateTimeOffset.UtcNow, threshold = options.Threshold, maxContextCharacters = input.MaxContextCharacters,
            contextPolicy = "whole items in supplied pool order; skip an item that does not fit; serialized JSON UTF-16 character budget",
            scope = "Controlled gate/context comparison over recorded candidate pools; no store search, source-support or answer-quality claim.",
            freshClientsPerDecision = true,
        };
        WriteNew(Path.Combine(outputDirectory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));

        async Task<MemoryGateDecision> Decide(MemoryGateRequest request, CancellationToken token)
        {
            var id = Interlocked.Increment(ref sequence);
            Emit(new { @event = "decision-started", id, request });
            using var meter = new CompoundHttpMeter(transportFactory?.Invoke() ?? new SocketsHttpHandler { AllowAutoRedirect = false });
            using var http = new HttpClient(meter) { Timeout = Timeout.InfiniteTimeSpan };
            var failures = new JudgeFailureCounter();
            var gate = new SystemOneMemoryGate(new SystemOneClient(http), Options.Create(options), failures);
            try
            {
                var decision = await gate.DecideAsync(request, token).ConfigureAwait(false);
                if (failures.Count != 0) throw new InvalidOperationException("A configured judge did not answer.");
                Emit(new { @event = "decision-finished", id, decision, counts = meter.Snapshot() with { JudgeFailures = failures.Count } });
                return decision;
            }
            catch (Exception ex)
            {
                Emit(new { @event = "decision-failed", id, error = ex.GetType().Name, counts = meter.Snapshot() with { JudgeFailures = failures.Count } });
                throw;
            }
        }
        try
        {
            var pair = await PairedGateEvaluation.RunAsync(input.Baseline, input.Overlay, options.Threshold,
                input.MaxContextCharacters, options.Timeout, Decide, cancellationToken).ConfigureAwait(false);
            var completed = pair.Baseline.Error is null && pair.Overlay.Error is null;
            WriteNew(Path.Combine(outputDirectory, "result.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                manifest.inputSha256, manifest.configurationSha256, manifest.scope, completed, pair,
            }, Json));
            Emit(new { @event = "run-finished", completed });
            return completed ? 0 : 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Emit(new { @event = "run-cancelled" });
            throw;
        }
        finally { lock (sync) closed = true; }
    }

    private static bool ValidJudge(SystemOneEndpoint? judge, string role)
    {
        if (judge is null || string.IsNullOrWhiteSpace(judge.Name) || string.IsNullOrWhiteSpace(judge.Model) ||
            !double.IsFinite(judge.Weight) || judge.Weight <= 0 || judge.Endpoint is not { IsAbsoluteUri: true } endpoint)
            return false;
        var local = endpoint.Scheme == "http" && endpoint.Host == "127.0.0.1" && endpoint.AbsolutePath == "/v1/systemone" &&
                    endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0 && endpoint.UserInfo.Length == 0 && judge.KeyVariable is null;
        return local || role == "development" && endpoint.AbsoluteUri == "https://api.typesafe.ai/v1/systemone" && judge.KeyVariable == "TYPESAFE_API_KEY";
    }

    private static void WriteNew(string path, byte[] value)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(value);
        file.Flush(flushToDisk: true);
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
