using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Reading;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>One pack test and how it went: the keys it found, in the order they came, and why it failed when it did.</summary>
[Experimental("AMEXT001")]
public sealed record PackTestResult(string Pack, string Name, bool Passed, IReadOnlyList<string> Found, string Detail);

/// <summary>
/// A pack's own unit tests (pack format v1.2 §7, <c>evals/tests.json</c>), deterministic and offline: each asks a view (a
/// search or a key) or the walk (anchors and a question) and names the record keys that must, and must not, come back.
/// They run on <see cref="PackMemory"/>, under the same rules a store applies.
/// </summary>
/// <remarks>
/// <code>
/// {"format": "agentmemory.pack.tests/1.2", "tests": [
///   {"name": "the first kata", "view": "kata", "search": "Which kata for 8th kyu?", "expect": ["shotokan/heian-shodan"], "top": 3},
///   {"name": "from the file to its tests", "anchors": [{"kind": "path", "value": "src/Map/MapStore.cs"}],
///    "question": "keep history", "expect": ["…"], "notExpect": ["…"]}]}
/// </code>
/// <c>module</c> names the module a view test reads (the pack's own by default); <c>top</c> requires each expected key within
/// the first that many results; <c>key</c> reads one record by its key instead of searching.
/// </remarks>
[Experimental("AMEXT001")]
public static class PackTests
{
    /// <summary>Where a pack keeps its tests.</summary>
    public const string FileName = "evals/tests.json";
    /// <summary>The tests file's format.</summary>
    public const string Format = "agentmemory.pack.tests/1.2";
    /// <summary>Most tests one file holds.</summary>
    public const int MaximumTests = 500;

    /// <summary>Runs the tests of <paramref name="pack"/> on <paramref name="memory"/>; empty when the pack has none.</summary>
    /// <exception cref="PackLoadException">The tests file is malformed.</exception>
    public static async Task<IReadOnlyList<PackTestResult>> RunAsync(LoadedPack pack, PackMemory memory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(memory);
        if (pack.Tests is not { } tests) return [];
        var results = new List<PackTestResult>();
        foreach (var test in Parse(tests.Span))
            results.Add(await RunAsync(pack.Id, test, memory, cancellationToken).ConfigureAwait(false));
        return results;
    }

    private sealed record PackTest(string Name, string? Module, string? View, string? Search, string? Key, IReadOnlyList<ContextAnchor> Anchors,
        string? Question, IReadOnlyList<string> Expect, IReadOnlyList<string> NotExpect, int? Top);

    private static List<PackTest> Parse(ReadOnlySpan<byte> bytes)
    {
        var problems = new List<string>();
        var tests = new List<PackTest>();
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.GetString() != Format)
                throw new PackLoadException([$"{FileName} must name format '{Format}'."]);
            if (!root.TryGetProperty("tests", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > MaximumTests)
                throw new PackLoadException([$"{FileName} holds 1 to {MaximumTests} tests."]);
            var index = 0;
            foreach (var item in list.EnumerateArray())
            {
                var at = $"{FileName} test {++index}";
                string? Text(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                IReadOnlyList<string> Keys(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                    ? [.. v.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!)] : [];
                var anchors = new List<ContextAnchor>();
                if (item.TryGetProperty("anchors", out var given) && given.ValueKind == JsonValueKind.Array)
                    foreach (var anchor in given.EnumerateArray())
                    {
                        var kind = anchor.TryGetProperty("kind", out var k) ? k.GetString() : null;
                        var value = anchor.TryGetProperty("value", out var v) ? v.GetString() : null;
                        if (value is null || kind is not ("path" or "symbol")) { problems.Add($"{at}: an anchor is {{\"kind\": \"path\" or \"symbol\", \"value\"}}."); continue; }
                        anchors.Add(kind == "path" ? ContextAnchor.Path(value) : ContextAnchor.Symbol(value));
                    }
                var test = new PackTest(Text("name") ?? $"test {index}", Text("module"), Text("view"), Text("search"), Text("key"), anchors,
                    Text("question"), Keys("expect"), Keys("notExpect"), item.TryGetProperty("top", out var top) && top.TryGetInt32(out var n) ? n : null);
                if ((test.View is null) == (test.Anchors.Count == 0)) problems.Add($"{at}: a test asks a view, or walks from anchors; exactly one.");
                if (test.View is not null && (test.Search is null) == (test.Key is null)) problems.Add($"{at}: a view test gives a search or a key.");
                if (test.Expect.Count == 0 && test.NotExpect.Count == 0) problems.Add($"{at}: a test names keys to expect or not to expect.");
                tests.Add(test);
            }
        }
        catch (JsonException e) { problems.Add($"{FileName} is not valid JSON: {e.Message}"); }
        if (problems.Count > 0) throw new PackLoadException(problems);
        return tests;
    }

    private static async Task<PackTestResult> RunAsync(string packId, PackTest test, PackMemory memory, CancellationToken cancellationToken)
    {
        var context = new ModuleReadContext { Scope = MemoryScope.For("pack-tests") };
        IReadOnlyList<string> found;
        if (test.View is { } view)
        {
            var module = test.Module ?? packId;
            if (memory.Resolution.Modules.All(m => m.Id != module)) return new(packId, test.Name, false, [], $"{module} is not loaded with the pack.");
            var result = await new ModuleViews(memory.Resolution, memory.ReaderFor(module))
                .EvaluateWithArgumentsAsync(view, new() { Search = test.Search, Key = test.Key }, context, cancellationToken).ConfigureAwait(false);
            if (result.Status != ModuleReadStatus.Succeeded) return new(packId, test.Name, false, [], $"the view refused: {string.Join("; ", result.Errors)}");
            found = [.. result.Items.Select(i => i.Record.Key)];
        }
        else
        {
            var walk = await TraversalWalk.RunAsync(memory.Resolution, memory,
                new ContextRequest { SessionId = "pack-tests", Owner = "pack-tests", Query = test.Question ?? "", Anchors = test.Anchors },
                cancellationToken).ConfigureAwait(false);
            if (walk.Problems.Count > 0 && walk.Items.Count == 0) return new(packId, test.Name, false, [], "the walk refused: " + string.Join("; ", walk.Problems));
            found = [.. walk.Items.Select(i => i.Record.Key)];
        }
        var window = test.Top is { } top ? found.Take(top).ToList() : found;
        var missing = test.Expect.Where(k => !window.Contains(k)).ToList();
        var unwanted = test.NotExpect.Where(found.Contains).ToList();
        var detail = missing.Count == 0 && unwanted.Count == 0 ? $"{found.Count} found"
            : string.Join("; ", new[]
            {
                missing.Count == 0 ? null : $"missing{(test.Top is { } t ? $" from the first {t}" : "")}: {string.Join(", ", missing)}",
                unwanted.Count == 0 ? null : $"should not come back: {string.Join(", ", unwanted)}",
            }.OfType<string>());
        return new(packId, test.Name, missing.Count == 0 && unwanted.Count == 0, found, detail);
    }
}
