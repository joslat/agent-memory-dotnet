using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ampack.Tests;

/// <summary>The conformance kit, found from this source file's own place, so the tests run wherever the build output goes.</summary>
internal static class Kit
{
    internal static readonly string Root = Path.Combine(Repository(), "conformance", "pack-v1.1");

    internal static string Repository([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    internal static byte[] Vector(string name) => File.ReadAllBytes(Path.Combine(Root, "vectors", name + ".ampack"));

    internal static byte[] Trust(string name = "trust.json") => File.ReadAllBytes(Path.Combine(Root, name));

    internal sealed record Case(string Vector, string Expected, string Policy, IReadOnlyList<string> Revocations);

    /// <summary>
    /// The vectors that are a pack, a trust policy and revocation documents: what Pack.Verify takes. The other kinds (receipts,
    /// certificates, erasures, aggregates, bare documents) go through the tools' options and are run by the kit itself.
    /// </summary>
    internal static (IReadOnlyList<Case> Plain, IReadOnlyList<string> MissingPolicy, int Total) Cases()
    {
        using var results = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "results.json")));
        var all = results.RootElement.GetProperty("vectors").EnumerateArray().ToList();
        var candidates = all.Where(v => v.GetProperty("kind").GetString() == "pack" && !v.TryGetProperty("location", out _) && !v.TryGetProperty("document", out _))
            .Select(v => new Case(v.GetProperty("vector").GetString()!, v.GetProperty("expected").GetString()!, v.GetProperty("policy").GetString()!,
                v.TryGetProperty("revocations", out var r) ? [.. r.EnumerateArray().Select(x => x.GetString()!)] : []))
            .ToList();
        // A policy that is deliberately not in the repository cannot be run, and a missing file must not count as a refusal.
        var missing = candidates.Where(c => !File.Exists(Path.Combine(Root, c.Policy))).Select(c => c.Vector).ToList();
        return ([.. candidates.Where(c => !missing.Contains(c.Vector))], missing, all.Count);
    }
}
