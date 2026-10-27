#pragma warning disable AMEXT001 // Onboarding is an Extensibility preview feature.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.Architecture;
using AgentMemory.Packs.Decisions;
using AgentMemory.Packs.Intent;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// CMP-9: onboarding an existing project. <c>onboard propose --pack decisions|architecture|intent --root &lt;repo&gt;</c> has a
/// model read the project's documents and adds what it proposes, each with the lines it read and a confidence, to the pack's
/// review set (<c>docs/memory/review/&lt;module&gt;.json</c>) as pending; <c>--dry-run</c> spends nothing: it prints what would
/// be sent and its cost, and runs the checks on sample replies. <c>onboard list</c> shows the set; <c>onboard decide --accept
/// k1,k2 | --reject k1 [--set name=value]</c> records a person's decision. The pack's scan, import and projection then merge
/// the accepted proposals into the deterministic snapshot; nothing pending or rejected reaches the store.
/// </summary>
internal sealed class OnboardCommand(TextWriter output)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Bitdeer GLM-5.3-Flash, USD per million tokens (the P6 run's rates); override with --price-in and --price-out.
    private const double PriceIn = 0.07, PriceOut = 0.25;

    private static readonly Dictionary<string, (Func<ModuleManifest> Manifest, string[] Types, string Guidance)> Packs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["decisions"] = (() => DecisionsPack.Manifest, ["DecisionsDecision"], """
            What counts: a choice the text says was made between alternatives, with or without its reason: "we decided", "chosen", "instead of",
            "rather than", "ruled", an ADR's decision, a design document's ratified decision.
            Never: a description of how the system behaves, a measurement or a table row of results, a feature, an instruction to the reader, a
            configuration option, or a plan not yet decided. Fill title (a short name of the choice) and decision (what was chosen).
            """),
        ["architecture"] = (() => ArchitecturePack.Manifest, ["ArchitectureSystem", "ArchitectureComponent"], """
            What counts: a system, or a component of it that is deployed, packaged or owned on its own: a service, an application, a library package,
            a project in the solution, a database or a queue it runs on.
            Never: a class, an interface, a method, a file, a configuration file or option, a test, a metric, a baseline, a document, or a concept.
            """),
        ["intent"] = (() => IntentPack.Manifest, ["IntentRequirement", "IntentIdea"], """
            What counts as a requirement: a sentence that states an obligation the product must keep, in normative words ("shall", "must", "must not",
            "is required to", "never"), or an item of a list the text calls requirements. Its text is that obligation, in EARS form when it fits.
            What counts as an idea: something the text explicitly marks as an idea, a future option, a proposal or a later step, not yet decided.
            Never: a description of what the code does today, an instruction to the reader, an example, a configuration default, a measurement,
            or a status note.
            """),
    };

    public async Task<int> ProposeAsync(string? pack, string? root, string? docs, string? types, string? review, bool dryRun, string? maxCalls,
        string? priceIn, string? priceOut, string? raw = null, CancellationToken cancellationToken = default)
    {
        if (pack is null || root is null || !Packs.TryGetValue(pack, out var known))
            return Usage("onboard propose needs --pack <decisions|architecture|intent> and --root <repository>.");
        var repository = Path.GetFullPath(root);
        var manifest = known.Manifest();
        var deterministic = Deterministic(pack, repository);
        var reviewPath = Path.Combine(repository, review ?? ModuleReview.DefaultPath(manifest.Id));
        var set = File.Exists(reviewPath) ? ModuleReview.Parse(File.ReadAllText(reviewPath)) : new ReviewSet { Module = manifest.Id };
        var documents = Documents(repository, docs);
        var options = new ProposerOptions
        {
            Types = types is null ? known.Types : [.. types.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)],
            Guidance = known.Guidance,
            MaximumCalls = maxCalls is null ? 40 : int.Parse(maxCalls, CultureInfo.InvariantCulture),
        };
        var prompts = ModuleProposer.Prompts(manifest, documents, deterministic, options);
        var sent = prompts.Take(options.MaximumCalls).ToList();
        var inputTokens = sent.Sum(p => (p.System.Length + p.User.Length) / 4L);
        var outputTokens = sent.Count * 1500L;
        var rateIn = priceIn is null ? PriceIn : double.Parse(priceIn, CultureInfo.InvariantCulture);
        var rateOut = priceOut is null ? PriceOut : double.Parse(priceOut, CultureInfo.InvariantCulture);
        string ReadFile(string path) => Path.Combine(repository, path) is var full && File.Exists(full) && IsInside(repository, full) ? File.ReadAllText(full) : null!;

        if (dryRun)
        {
            // Every path a real reply can take, on replies built from the first chunk's own lines: a proposal that holds, one
            // quoting text that is not there, one of a type the run may not propose, and one with a property the type lacks.
            var samples = Samples(sent.FirstOrDefault().User, options.Types[0]);
            var checks = samples.Select(s => (s.Case, Outcome: ModuleProposer.ReadReply(s.Reply, manifest, deterministic, options, ReadFile, DateTimeOffset.UtcNow)))
                .Select(r => new { r.Case, kept = r.Outcome.Kept.Count, dropped = r.Outcome.Dropped }).ToList();
            output.WriteLine(JsonSerializer.Serialize(new
            {
                dryRun = true, module = manifest.Id, types = options.Types, documents = documents.Count, documentFiles = documents.Select(d => d.Path).Take(40),
                deterministicRecords = deterministic.Records.Count, reviewSet = Relative(repository, reviewPath), alreadyInSet = set.Items.Count,
                calls = sent.Count, chunksBeyondBudget = Math.Max(0, prompts.Count - options.MaximumCalls),
                estimatedInputTokens = inputTokens, estimatedOutputTokens = outputTokens,
                estimatedUsd = Math.Round(inputTokens / 1e6 * rateIn + outputTokens / 1e6 * rateOut, 4),
                firstPrompt = sent.Count == 0 ? null : new { system = sent[0].System, userHead = Head(sent[0].User, 600) },
                checks,
            }, Json));
            var expected = new[] { 1, 0, 0, 0 };
            return sent.Count > 0 && checks.Select(c => c.kept).SequenceEqual(expected) ? 0 : 1;
        }

        var resolution = AgentMemory.Inference.InferenceProviderEnvironment.Resolve();
        if (resolution.Settings is not { } settings) return Usage("no model is configured: " + resolution.Diagnostic);
        var model = settings.EffectiveExtractionModel;
        if (!AgentMemory.Inference.InferenceClientFactory.TryCreateChatClient(settings, "onboarding", out var client, out var diagnostic, model, generousTimeout: true))
            return Usage("no model client: " + diagnostic);
        using (client as IDisposable)
        {
            var clock = Stopwatch.StartNew();
            if (raw is not null) Directory.CreateDirectory(raw);
            var run = await ModuleProposer.ProposeAsync(client, manifest, documents, deterministic, options with
                {
                    ProposedBy = "model:" + model,
                    OnReply = raw is null ? null : (call, text) => File.WriteAllText(Path.Combine(raw, $"reply-{call:000}.txt"), text),
                },
                ReadFile, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            var (updated, added, leftOut) = ModuleReview.Propose(set, run.Proposed, deterministic);
            Directory.CreateDirectory(Path.GetDirectoryName(reviewPath)!);
            File.WriteAllText(reviewPath, ModuleReview.Write(updated), new System.Text.UTF8Encoding(false));
            output.WriteLine(JsonSerializer.Serialize(new
            {
                module = manifest.Id, model, provider = settings.Provider.ToString(), calls = run.Calls, chunksBeyondBudget = run.ChunksSkipped,
                inputTokens = run.InputTokens, outputTokens = run.OutputTokens,
                usd = Math.Round(run.InputTokens / 1e6 * rateIn + run.OutputTokens / 1e6 * rateOut, 4), seconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
                proposed = run.Proposed.Count, added, leftOut, dropped = run.Dropped.Count, droppedFirst = run.Dropped.Take(12),
                reviewSet = Relative(repository, reviewPath), pending = updated.Items.Count(i => i.State == ReviewState.Pending),
            }, Json));
        }
        return 0;
    }

    public int List(string? pack, string? root, string? review, string? state)
    {
        if (pack is null || root is null || !Packs.TryGetValue(pack, out var known)) return Usage("onboard list needs --pack <decisions|architecture|intent> and --root <repository>.");
        var repository = Path.GetFullPath(root);
        var path = Path.Combine(repository, review ?? ModuleReview.DefaultPath(known.Manifest().Id));
        if (!File.Exists(path)) return Usage($"no review set at {Relative(repository, path)}; run onboard propose first.");
        var set = ModuleReview.Parse(File.ReadAllText(path));
        var filter = state is null ? (ReviewState?)null : Enum.Parse<ReviewState>(state, ignoreCase: true);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            module = set.Module, byState = set.Items.GroupBy(i => i.State.ToString().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count()),
            items = set.Items.Where(i => filter is null || i.State == filter).Select(i => new
            {
                key = $"{i.Type}:{i.Key}", state = i.State.ToString().ToLowerInvariant(), i.Confidence,
                title = i.Properties.TryGetValue("title", out var t) ? t.ToString() : null,
                evidence = i.Evidence.Select(e => $"{e.File}:{e.Line}-{e.EndLine} \"{Head(e.Quote, 120)}\""), i.ProposedBy, i.ReviewedBy, i.Note,
            }),
        }, Json));
        return 0;
    }

    public int Decide(string? pack, string? root, string? review, string? accept, string? reject, string? note, string? by, string? edit)
    {
        if (pack is null || root is null || !Packs.TryGetValue(pack, out var known) || (accept is null) == (reject is null))
            return Usage("onboard decide needs --pack, --root and one of --accept k1,k2 or --reject k1,k2.");
        var repository = Path.GetFullPath(root);
        var path = Path.Combine(repository, review ?? ModuleReview.DefaultPath(known.Manifest().Id));
        if (!File.Exists(path)) return Usage($"no review set at {Relative(repository, path)}.");
        var set = ModuleReview.Parse(File.ReadAllText(path));
        Dictionary<string, JsonElement>? edits = null;
        if (edit is not null)
        {
            // --edit '{"title":"…","status":null}': the properties to change; null removes one.
            using var document = JsonDocument.Parse(edit);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Usage("--edit takes a JSON object of the properties to change.");
            edits = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        }
        var keys = (accept ?? reject)!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        try
        {
            set = ModuleReview.Decide(set, keys, accept is not null, by ?? Environment.UserName, DateTimeOffset.UtcNow, note, edits);
        }
        catch (ArgumentException e) { return Usage(e.Message); }
        // An edit must still be a record the module takes.
        var merged = ModuleReview.Merge(Deterministic(pack, repository), set, out var conflicts);
        if (merged.Problems() is { Count: > 0 } problems) return Usage("the decision would make a record the pack refuses: " + problems[0]);
        File.WriteAllText(path, ModuleReview.Write(set), new System.Text.UTF8Encoding(false));
        output.WriteLine(JsonSerializer.Serialize(new
        {
            module = set.Module, decided = keys, accepted = accept is not null, conflicts,
            byState = set.Items.GroupBy(i => i.State.ToString().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count()),
        }, Json));
        return 0;
    }

    /// <summary>A pack's deterministic snapshot with its review set's accepted proposals merged in, when the repository has one.</summary>
    internal static ModuleSnapshot Reviewed(ModuleSnapshot snapshot, string root)
    {
        var path = Path.Combine(root, ModuleReview.DefaultPath(snapshot.Module.Id));
        if (!File.Exists(path)) return snapshot;
        var merged = ModuleReview.Merge(snapshot, ModuleReview.Parse(File.ReadAllText(path)), out var conflicts);
        foreach (var conflict in conflicts) Console.Error.WriteLine($"note: {conflict} is accepted in the review set and also read from the project's files; the files win.");
        return merged;
    }

    private static ModuleSnapshot Deterministic(string pack, string root) => pack.ToLowerInvariant() switch
    {
        "decisions" => DecisionsCommand.Read(root, null, out _, reviewed: false),
        "architecture" => ArchitectureCommand.Read(root, null, out _, out _, reviewed: false),
        _ => IntentCommand.Read(root, null, out _, out _, reviewed: false),
    };

    // The project's prose: README files and Markdown under docs/, tracked, never the memory's own projections or review sets.
    private static List<ProposerDocument> Documents(string root, string? globs)
    {
        var tracked = Git(root, "ls-files", "-z") is { } text ? text.Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
        var patterns = (globs ?? "README.md,docs/**/*.md").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var include = patterns.Select(Glob).ToList();
        var memory = Glob("docs/memory/**");
        return [.. tracked.Where(f => include.Any(g => g.IsMatch(f)) && !memory.IsMatch(f)).Order(StringComparer.Ordinal)
            .Select(f => new ProposerDocument(f, File.ReadAllText(Path.Combine(root, f))))];
    }

    // A path glob: ** crosses folders, * and ? stay within one.
    private static System.Text.RegularExpressions.Regex Glob(string pattern) =>
        new(@"\A" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*\*/", "(.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal).Replace(@"\*", "[^/]*", StringComparison.Ordinal).Replace(@"\?", "[^/]", StringComparison.Ordinal) + @"\z",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // Replies a model could give, built from a chunk's own first line with text.
    private static IReadOnlyList<(string Case, string Reply)> Samples(string? chunk, string type)
    {
        var line = (chunk ?? "").Split('\n').FirstOrDefault(l => l.Contains("| ", StringComparison.Ordinal) && l[(l.IndexOf("| ", StringComparison.Ordinal) + 2)..].Trim().Length >= 12) ?? "README.md:1| (no text)";
        var bar = line.IndexOf("| ", StringComparison.Ordinal);
        var location = line[..bar];
        var colon = location.LastIndexOf(':');
        var file = location[..colon];
        var number = location[(colon + 1)..];
        var quote = line[(bar + 2)..].Trim();
        quote = quote.Length > 60 ? quote[..60] : quote;
        string Reply(string recordType, string properties, string quoted) =>
            $$"""{"records":[{"type":{{JsonSerializer.Serialize(recordType)}},"key":"sample-record","properties":{{properties}},"evidence":[{"file":{{JsonSerializer.Serialize(file)}},"line":{{number}},"endLine":{{number}},"quote":{{JsonSerializer.Serialize(quoted)}}}],"confidence":0.7}]}""";
        var title = $$"""{"title":{{JsonSerializer.Serialize(quote)}}}""";
        return
        [
            ("holds", Reply(type, Fill(type, quote), quote)),
            ("quote not in the file", Reply(type, Fill(type, quote), "this sentence was never written in the project")),
            ("type not allowed", Reply("NoSuchType", title, quote)),
            ("property the type lacks", Reply(type, Fill(type, quote)[..^1] + ""","noSuchProperty":"x"}""", quote)),
        ];
    }

    // The required properties of a sample record, filled with the quote (or the first allowed value).
    private static string Fill(string type, string quote)
    {
        var declared = Packs.Values.Select(p => p.Manifest()).SelectMany(m => m.Ontology?.Types ?? []).First(t => t.Name == type);
        var values = declared.Properties.Where(p => p.Value.Required && p.Key is not ("source" or "file" or "line"))
            .ToDictionary(p => p.Key, p => p.Value.Values.Count > 0 ? (object)p.Value.Values[0] : p.Value.Type == "string" ? quote : 1);
        if (declared.Properties.ContainsKey("title")) values.TryAdd("title", quote);
        return JsonSerializer.Serialize(values);
    }

    private static bool IsInside(string root, string full) =>
        Path.GetFullPath(full).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Head(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    private static string? Git(string root, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-C"); start.ArgumentList.Add(root);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? text : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    private int Usage(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 2;
    }
}
