using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Examples.RepositoryLessons;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Inference;
using Microsoft.Extensions.AI;

namespace AgentMemory.RouterArena.Ask;

/// <summary>
/// EXT-measure M1 (strategy performance/prereg/PREREG-EXT-MEASURE-2026-10-25.md): the Repository Lessons writer on the coding
/// pilot's eight frozen digests, one call a digest, each repository's lessons so far shown as its existing records through the
/// module's own view. Admission is the SDK's; nothing is closed (no approver); no database.
/// </summary>
/// <remarks>The three-stage protocol: <c>--dry-run</c> prints the first call and spends nothing; <c>--limit 1</c> asks one
/// digest; then the rest. Digests already answered are not asked again.</remarks>
internal sealed class LessonRetention(TextWriter output)
{
    private static readonly string[] Repositories = ["matplotlib__matplotlib", "scikit-learn__scikit-learn", "sympy__sympy"];

    public async Task<int> RunAsync(string evidenceZip, string outPath, bool dryRun, int? limit, string? model, CancellationToken cancellationToken = default)
    {
        var digests = Digests(evidenceZip);
        var resolution = ModuleResolver.Resolve([Manifest()], ModuleVersion.Parse("0.1.0"),
            new HashSet<string>(StringComparer.Ordinal) { ModuleHostFeatures.ModuleSchema, ModuleHostFeatures.Inference });
        if (!resolution.Succeeded)
        {
            output.WriteLine("error: ext-lessons: " + string.Join("; ", resolution.Errors));
            return 1;
        }
        var cache = File.Exists(outPath)
            ? JsonNode.Parse(await File.ReadAllTextAsync(outPath, cancellationToken).ConfigureAwait(false))!.AsObject()
            : new JsonObject
            {
                ["format"] = "ext-measure-m1/1", ["module"] = RepositoryLessonsModule.Id + "@" + Manifest().Version,
                ["evidenceSha256"] = Sha256(evidenceZip), ["digests"] = new JsonObject(),
            };
        var done = cache["digests"]!.AsObject();
        var todo = digests.Where(d => done[d.Instance]?["lessons"] is null).ToList();
        if (limit is { } n) todo = [.. todo.Take(n)];

        IChatClient? chat = null;
        if (!dryRun)
        {
            var resolved = InferenceProviderEnvironment.Resolve(Environment.GetEnvironmentVariable);
            if (resolved.Settings is not { } settings
                || !InferenceClientFactory.TryCreateChatClient(settings, "lesson writer", out chat, out var why, model: model, generousTimeout: true))
            {
                output.WriteLine($"error: ext-lessons: no chat model ({resolved.Diagnostic})");
                return 1;
            }
            cache["model"] = model ?? settings.Model;
            cache["provider"] = settings.Provider.ToString();
        }

        foreach (var digest in todo)
        {
            // Each repository's own lessons so far, as the module's view renders them: the replay of every earlier answer.
            var kept = Kept(digests.TakeWhile(d => d.Instance != digest.Instance).Where(d => d.Repository == digest.Repository), done, resolution);
            var request = new IngestionRequest
            {
                SessionId = digest.Session, Owner = "pilot", AtUtc = DateTimeOffset.Parse("2026-10-14T00:00:00Z"),
                Messages = [new() { Id = digest.Instance + ":digest", Role = "user", Text = digest.Text }],
                Existing = await ExistingAsync(kept, resolution, cancellationToken).ConfigureAwait(false),
            };
            var options = new RepositoryLessonsOptions { Repository = digest.RepositoryName };
            if (dryRun)
            {
                var prompt = LessonWriter.Prompt(request, options.Repository);
                output.WriteLine(prompt[1].Text);
                var tokens = prompt.Sum(m => m.Text.Length) / 4;
                output.WriteLine($"ext-lessons: {todo.Count} digests to ask, one call each; about {tokens:N0} tokens in for this one, "
                    + $"about {todo.Count * (tokens * MeteredChatClient.InPrice + 6_000 * MeteredChatClient.OutPrice) / 1e6:0.###} USD at most; nothing was sent.");
                return 0;
            }
            var metered = new MeteredChatClient(chat!);
            var writer = new LessonWriter(metered, options);
            var watch = Stopwatch.StartNew();
            var entry = new JsonObject
            {
                ["repository"] = digest.RepositoryName, ["termination"] = digest.Termination, ["existingShown"] = request.Existing.Count,
            };
            try
            {
                var proposals = await writer.ProposeAsync(request, cancellationToken).ConfigureAwait(false);
                var lessons = new JsonArray();
                foreach (var proposal in proposals)
                {
                    var admitted = ModuleProposalAdmission.Admit(resolution, writer.Descriptor, request, proposal);
                    lessons.Add(new JsonObject
                    {
                        ["key"] = proposal.Key,
                        ["properties"] = JsonSerializer.SerializeToNode(proposal.Properties),
                        ["replaces"] = JsonSerializer.SerializeToNode(proposal.Replaces),
                        ["admitted"] = admitted.Accepted,
                        ["errors"] = JsonSerializer.SerializeToNode(admitted.Errors),
                    });
                }
                entry["lessons"] = lessons;
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                entry["error"] = $"{exception.GetType().Name}: {exception.Message}";
                entry["lessons"] = new JsonArray();
            }
            entry["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 2);
            entry["tokensIn"] = metered.In;
            entry["tokensOut"] = metered.Out;
            entry["cost"] = metered.Cost;
            done[digest.Instance] = entry;
            await File.WriteAllTextAsync(outPath, cache.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
            output.WriteLine($"  {digest.Instance}: {((JsonArray)entry["lessons"]!).Count} lessons{(entry["error"] is { } e ? $" ({e})" : "")}, "
                + $"{entry["seconds"]} s, {metered.Out} tokens out");
        }

        var all = done.Select(d => d.Value!).ToList();
        var lessonsOf = all.Select(d => ((JsonArray)d["lessons"]!).Where(l => (bool)l!["admitted"]!).ToList()).ToList();
        output.WriteLine($"ext-lessons: {all.Count} of {digests.Count} digests asked; {lessonsOf.Count(l => l.Count > 0)} kept at least one lesson; "
            + $"{lessonsOf.Sum(l => l.Count)} lessons admitted, {lessonsOf.Sum(l => l.Count(x => x!["properties"]!["failure"] is not null || x["properties"]!["fix"] is not null))} "
            + $"with a failure or fix; cost {all.Sum(d => (double?)d["cost"] ?? 0):0.####} USD");
        return 0;
    }

    /// <summary>
    /// The module's embedded manifest, the same bytes the package carries. Read directly: discovery (<c>ModuleManifest.Load</c>)
    /// rightly refuses this arena assembly, whose version is the repository's, not the module's.
    /// </summary>
    private static ModuleManifest Manifest()
    {
        using var stream = typeof(LessonRetention).Assembly.GetManifestResourceStream("RepositoryLessons.agentmemory-module.json")
            ?? throw new InvalidOperationException("The Repository Lessons manifest is not embedded.");
        using var reader = new StreamReader(stream);
        return ModuleManifest.Parse(reader.ReadToEnd());
    }

    private sealed record Digest(string Repository, string Instance, string Session, string Text, string RepositoryName, string Termination);

    private static List<Digest> Digests(string evidenceZip)
    {
        using var zip = ZipFile.OpenRead(evidenceZip);
        var digests = new List<Digest>();
        foreach (var repository in Repositories)
        {
            var entry = zip.GetEntry($"artifacts/cf-benchmark-20261014/history-capture-inputs-v2/{repository}/remember-requests.json")
                ?? throw new FileNotFoundException("Digest inputs missing for " + repository);
            using var stream = entry.Open();
            foreach (var item in JsonNode.Parse(stream)!.AsArray())
            {
                var text = (string)item!["request"]!["userText"]!;
                string Line(string name) => text.Split('\n').FirstOrDefault(l => l.StartsWith(name + ": ", StringComparison.Ordinal))?[(name.Length + 2)..].Trim() ?? "unknown";
                digests.Add(new(repository, (string)item["instance"]!, (string)item["request"]!["session"]!, text, Line("Repository"), Line("Recorded termination")));
            }
        }
        return digests;
    }

    /// <summary>The admitted lessons of the given earlier digests, latest version per key, as stored records.</summary>
    private static List<ModuleRecord> Kept(IEnumerable<Digest> earlier, JsonObject done, ModuleResolution resolution)
    {
        var byKey = new Dictionary<string, ModuleRecord>(StringComparer.Ordinal);
        foreach (var digest in earlier)
            foreach (var lesson in done[digest.Instance]?["lessons"]?.AsArray() ?? [])
            {
                if (lesson is null || !(bool)lesson["admitted"]!) continue;
                var key = (string)lesson["key"]!;
                foreach (var replaced in lesson["replaces"]!.AsArray()) byKey.Remove((string)replaced!);
                byKey[key] = new ModuleRecord
                {
                    ModuleId = RepositoryLessonsModule.Id, Type = RepositoryLessonsModule.Type, Id = digest.Instance + ":" + key, Key = key,
                    OwnerId = "pilot", CreatedAtUtc = DateTimeOffset.Parse("2026-10-14T00:00:00Z"), SourceSessionId = digest.Session,
                    Source = new() { MessageIds = [digest.Instance + ":digest"], Excerpt = "digest" },
                    Properties = lesson["properties"].Deserialize<Dictionary<string, JsonElement>>()!,
                };
            }
        _ = resolution;
        return [.. byKey.Values];
    }

    /// <summary>The kept lessons through the module's own view, so the writer sees them as production renders them.</summary>
    private static async Task<IReadOnlyList<ModuleViewItem>> ExistingAsync(IReadOnlyList<ModuleRecord> kept, ModuleResolution resolution,
        CancellationToken cancellationToken)
    {
        var result = await new ModuleViews(resolution, new KeptReader(kept)).EvaluateAsync(RepositoryLessonsModule.CurrentView,
            new ModuleReadContext { Scope = Abstractions.Options.MemoryScope.For("pilot") }, cancellationToken).ConfigureAwait(false);
        return result.Status == ModuleReadStatus.Succeeded ? result.Items : throw new InvalidOperationException(string.Join("; ", result.Errors));
    }

    private sealed class KeptReader(IReadOnlyList<ModuleRecord> kept) : IModuleReader
    {
        public string CallerModuleId => RepositoryLessonsModule.Id;

        public Task<ModuleReadResult> FindAsync(ModuleQuery query, ModuleReadContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModuleReadResult { Status = ModuleReadStatus.Succeeded, Records = [.. kept.Take(query.Limit)] });
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
