// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>One thing a content scan found: where (file, line, JSON path), what kind and by which rule, and how serious.</summary>
/// <param name="File">The pack file.</param>
/// <param name="Line">The line in a JSONL file, 1-based; 0 for a JSON file.</param>
/// <param name="Path">The JSON path of the string, <c>$.properties.note</c>.</param>
/// <param name="Category">personal-data, secret or injection.</param>
/// <param name="Rule">The rule, such as <c>personal-data.email</c>.</param>
/// <param name="Severity">low, medium or high.</param>
/// <param name="Sample">The match, masked to its first and last characters, so a report never repeats what it found.</param>
[Experimental("AMEXT001")]
public sealed record PackScanFinding(string File, int Line, string Path, string Category, string Rule, string Severity, string Sample);

/// <summary>A content scan of one pack: how many strings it read and what it found.</summary>
[Experimental("AMEXT001")]
public sealed record PackScanReport(int Files, int Strings, IReadOnlyList<PackScanFinding> Findings)
{
    /// <summary>Nothing found.</summary>
    public bool Clean => Findings.Count == 0;
}

/// <summary>
/// PACK-scan: what a pack's records and declarations carry that should not travel in a memory, found before it does. Personal
/// data (e-mail addresses, phone numbers, payment card numbers that pass the Luhn check, IBANs that pass mod 97); secrets
/// (private keys, cloud and API tokens); and prompt-injection payloads (the marker phrases AgentEval's gates use, chat
/// template tokens, invisible Unicode that hides text from a reader: tag characters, zero-width and bidirectional controls,
/// and markdown images whose link carries data out). Every pattern runs without backtracking, so a scan is linear in the
/// pack. A clean scan written as AEF evidence (<see cref="WriteRun"/>) is what an assurer seals, signs and countersigns.
/// </summary>
[Experimental("AMEXT001")]
public static class PackScan
{
    private const RegexOptions Linear = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    private static readonly (string Rule, string Category, string Severity, Regex Pattern, Func<string, bool>? Confirm)[] Rules =
    [
        ("personal-data.email", "personal-data", "medium", new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", Linear), null),
        ("personal-data.phone", "personal-data", "medium", new Regex(@"(\+[1-9][0-9]{7,14}|\b[0-9]{3}[-. ][0-9]{3}[-. ][0-9]{4}\b)", Linear), null),
        ("personal-data.payment-card", "personal-data", "high", new Regex(@"\b[0-9]{4}([ -]?[0-9]{4}){2}[ -]?[0-9]{1,7}\b", Linear), Luhn),
        ("personal-data.iban", "personal-data", "high", new Regex(@"\b[A-Z]{2}[0-9]{2}( ?[A-Z0-9]{4}){2,7}( ?[A-Z0-9]{1,4})?\b", Linear), Iban),
        ("personal-data.us-ssn", "personal-data", "high", new Regex(@"\b[0-9]{3}-[0-9]{2}-[0-9]{4}\b", Linear), null),
        ("secret.private-key", "secret", "high", new Regex(@"-----BEGIN ([A-Z]+ )?PRIVATE KEY-----", Linear), null),
        ("secret.aws-access-key", "secret", "high", new Regex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", Linear), null),
        ("secret.github-token", "secret", "high", new Regex(@"\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36}\b", Linear), null),
        ("secret.api-key", "secret", "high", new Regex(@"\b(sk|rk)-[A-Za-z0-9_-]{20,}\b", Linear), null),
        ("injection.chat-template", "injection", "high", new Regex(@"(<\|(im_start|im_end|system|user|assistant|endoftext)\|>|\[/?INST\]|<<SYS>>)", Linear), null),
        ("injection.markdown-exfiltration", "injection", "medium", new Regex(@"!\[[^\]\n]{0,200}\]\(https?://[^)\s]{1,500}\?[^)\s]{1,2000}\)", Linear), null),
    ];

    /// <summary>The marker phrases AgentEval's injection gates block (TokenInjectionGate.DefaultTokens), and a few a memory must not carry either.</summary>
    public static readonly IReadOnlyList<string> InjectionMarkers =
    [
        "ignore previous instructions", "ignore all previous instructions", "disregard the above", "you are now", "system prompt:",
        "reveal your instructions", "ignore the above", "new instructions:", "do not tell the user",
    ];

    /// <summary>Scans a pack read from a folder or an <c>.ampack</c> file: every JSON and JSONL file it lists, signatures aside.</summary>
    /// <exception cref="PackLoadException">The pack cannot be read under the strict profile.</exception>
    public static PackScanReport Scan(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try { return Scan(PackReader.Read(path).Files, verified: false); }
        catch (Exception e) when (e is InvalidDataException or IOException) { throw new PackLoadException([$"{path}: {e.Message}"]); }
    }

    /// <summary>Scans an <c>.ampack</c> held in memory.</summary>
    /// <exception cref="PackLoadException">The archive cannot be read under the strict profile.</exception>
    public static PackScanReport Scan(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        try { return Scan(PackReader.ReadArchive(archive, "archive").Files, verified: false); }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
    }

    /// <summary>
    /// Scans the files of a pack already read once; <paramref name="verified"/> when the caller verified those very bytes. A
    /// pack read as it is may hold no more than verification would parse (review 7, D2).
    /// </summary>
    /// <exception cref="PackLoadException">The pack is larger than an unverified pack may be, or a file or line than any.</exception>
    internal static PackScanReport Scan(IReadOnlyDictionary<string, byte[]> files, bool verified)
    {
        if (PackContentBounds.Problems(files, verified) is { Count: > 0 } problems) throw new PackLoadException(problems);
        var findings = new List<PackScanFinding>();
        var scannedFiles = 0;
        var strings = 0;
        foreach (var (file, bytes) in files.Where(f => !f.Key.StartsWith("META/", StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var lines = file.EndsWith(".jsonl", StringComparison.Ordinal) ? Encoding.UTF8.GetString(bytes).Split('\n')
                : file.EndsWith(".json", StringComparison.Ordinal) ? [Encoding.UTF8.GetString(bytes)] : null;
            if (lines is null) continue;
            scannedFiles++;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(lines[i]); }
                catch (JsonException) { continue; } // lint and verification refuse what is not JSON; the scan reads what is
                var line = file.EndsWith(".jsonl", StringComparison.Ordinal) ? i + 1 : 0;
                Walk(node, [], (segments, value) =>
                {
                    strings++;
                    string? path = null;
                    foreach (var (rule, category, severity, sample) in Check(value))
                        findings.Add(new(file, line, path ??= "$" + string.Concat(segments), category, rule, severity, sample));
                });
            }
        }
        return new PackScanReport(scannedFiles, strings, findings);
    }

    // Every string in a JSON value, keys included, with the segments of its path: the path is written out only for a finding,
    // since a key can be as long as the file (the fourth review's lesson on the JSON check).
    private static void Walk(JsonNode? node, List<string> segments, Action<List<string>, string> visit)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    visit(segments, key);
                    segments.Add("." + key);
                    Walk(value, segments, visit);
                    segments.RemoveAt(segments.Count - 1);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    segments.Add("[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                    Walk(a[i], segments, visit);
                    segments.RemoveAt(segments.Count - 1);
                }
                break;
            case JsonValue v when v.TryGetValue<string>(out var text):
                visit(segments, text);
                break;
        }
    }

    /// <summary>What one string carries: rule, category, severity and a masked sample, for each finding.</summary>
    public static IEnumerable<(string Rule, string Category, string Severity, string Sample)> Check(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var (rule, category, severity, pattern, confirm) in Rules)
            foreach (Match match in pattern.Matches(text))
                if (confirm is null || confirm(match.Value))
                {
                    yield return (rule, category, severity, Mask(match.Value));
                    break; // one finding per rule and string is enough to act on
                }
        foreach (var marker in InjectionMarkers)
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                yield return ("injection.marker", "injection", "high", marker);
                break;
            }
        if (Hidden(text) is { } hidden) yield return (hidden.Rule, "injection", "high", hidden.Sample);
    }

    // Unicode that hides text from a reader but not from a model: tag characters (ASCII smuggling), zero-width characters,
    // bidirectional embeddings and isolates (Trojan Source). Not ZWNJ and ZWJ, which the display rule keeps: Persian, Indic
    // scripts and joined emoji need them (feature 04 review, M5).
    private static (string Rule, string Sample)? Hidden(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.ConvertToUtf32(c, text[i + 1]) is >= 0xE0000 and <= 0xE007F)
                return ("injection.unicode-tags", "U+" + char.ConvertToUtf32(c, text[i + 1]).ToString("X5", CultureInfo.InvariantCulture));
            if (c is '​' or '⁠' or '﻿')
                return ("injection.zero-width", "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture));
            if (c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩')
                return ("injection.bidi-control", "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }
        return null;
    }

    private static string Mask(string value) => value.Length <= 4 ? new string('*', value.Length) : value[0] + new string('*', Math.Min(value.Length - 2, 12)) + value[^1];

    // A payment card number: 13 to 19 digits whose Luhn checksum holds.
    private static bool Luhn(string value)
    {
        var digits = value.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    // An IBAN: 15 to 34 characters whose mod 97 checksum is 1.
    private static bool Iban(string value)
    {
        var compact = value.Replace(" ", "", StringComparison.Ordinal);
        if (compact.Length is < 15 or > 34) return false;
        var rearranged = compact[4..] + compact[..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            var number = char.IsAsciiDigit(c) ? c - '0' : char.IsAsciiLetterUpper(c) ? c - 'A' + 10 : -1;
            if (number < 0) return false;
            remainder = (number >= 10 ? remainder * 100 : remainder * 10) + number;
            remainder %= 97;
        }
        return remainder == 1;
    }

    /// <summary>
    /// Writes a scan as an AEF run in <paramref name="folder"/> (absent or empty), closed and unsealed for an assurer to seal and
    /// sign: the pack as the subject, one result per rule (passed when it found nothing), counts and locations under
    /// <c>io.agentmemory.scan</c> and never what was found (<c>contentCapture: off</c>), and a gate line: ship when clean.
    /// </summary>
    public static string WriteRun(PackScanReport report, string packId, string packVersion, string packDigest, DateTimeOffset scannedAt, string folder,
        string producerVersion = "2.0.0")
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()) throw new ArgumentException($"{folder} is not empty.", nameof(folder));
        Directory.CreateDirectory(folder);
        var at = scannedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        var runId = $"scan.{packId}.{packVersion}.{scannedAt.UtcDateTime:yyyyMMddTHHmmssfffZ}";
        if (runId.Length > 128 || runId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-'))) runId = "scan." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant()[..40];
        List<(string Rule, string Category)> rules =
        [
            .. Rules.Select(r => (r.Rule, r.Category)),
            ("injection.marker", "injection"), ("injection.unicode-tags", "injection"), ("injection.zero-width", "injection"), ("injection.bidi-control", "injection"),
        ];
        var results = new List<JsonObject>();
        foreach (var (rule, category) in rules)
        {
            var found = report.Findings.Where(f => f.Rule == rule).ToList();
            results.Add(new JsonObject
            {
                ["schemaVersion"] = PackTrialEvidence.SchemaVersion, ["resultId"] = PackTrialEvidence.ResultId(runId, rule, "scan"), ["caseId"] = rule, ["path"] = "scan",
                ["evaluator"] = new JsonObject { ["id"] = "agentmemory.pack-scan", ["version"] = "1" }, ["state"] = found.Count == 0 ? "passed" : "failed",
                ["ext"] = new JsonObject
                {
                    ["io.agentmemory.scan"] = new JsonObject
                    {
                        ["category"] = category, ["findings"] = found.Count,
                        ["at"] = new JsonArray([.. found.Take(100).Select(f => (JsonNode)$"{f.File}:{f.Line}:{f.Path}")]),
                    },
                },
            });
            if (found.Count > 0) results[^1]["severity"] = found.Any(f => f.Severity == "high") ? "high" : found.Any(f => f.Severity == "medium") ? "medium" : "low";
        }
        var passed = results.Count(r => r["state"]!.GetValue<string>() == "passed");
        var run = new JsonObject
        {
            ["schemaVersion"] = PackTrialEvidence.SchemaVersion, ["runId"] = runId, ["status"] = "completed",
            ["producer"] = new JsonObject { ["name"] = "agentmemory-pack-scan", ["version"] = producerVersion },
            ["subject"] = new JsonObject { ["ref"] = $"pack:{packId}@{packDigest}", ["kind"] = "other", ["version"] = packVersion },
            ["suite"] = new JsonObject { ["ref"] = "scan:agentmemory/content", ["version"] = "1" },
            ["execution"] = new JsonObject { ["targetMode"] = "live", ["stimulus"] = "suite" },
            ["startedAt"] = at, ["endedAt"] = at, ["contentCapture"] = "off",
            ["ext"] = new JsonObject { ["io.agentmemory.scan"] = new JsonObject { ["files"] = report.Files, ["strings"] = report.Strings } },
        };
        var metrics = new JsonObject
        {
            ["schemaVersion"] = PackTrialEvidence.SchemaVersion,
            ["metrics"] = new JsonArray(new JsonObject
            {
                ["id"] = "clean", ["kind"] = "rate", ["direction"] = "higher_better", ["scale"] = new JsonObject { ["min"] = 0, ["max"] = 1 },
                ["description"] = "Whether a rule found nothing in the pack.",
            }),
        };
        var summary = new JsonObject
        {
            ["schemaVersion"] = PackTrialEvidence.SchemaVersion, ["runId"] = runId,
            ["lanes"] = new JsonArray(new JsonObject
            {
                ["lane"] = "content",
                ["metrics"] = new JsonArray(new JsonObject
                {
                    ["metric"] = "clean", ["path"] = "scan", ["N"] = results.Count, ["n"] = results.Count, ["notMeasured"] = 0,
                    ["sum"] = (double)passed, ["value"] = (double)passed / results.Count, ["verdict"] = report.Clean ? "passed" : "failed",
                }),
            }),
        };
        var gate = new JsonObject
        {
            ["schemaVersion"] = PackTrialEvidence.SchemaVersion, ["gateId"] = "gate:agentmemory/pack-scan", ["decisionId"] = runId + ".decision",
            ["rule"] = new JsonObject { ["strategy"] = "no finding of any rule", ["inputs"] = new JsonArray([.. rules.Select(r => (JsonNode)r.Rule)]) },
            ["inputs"] = new JsonObject { ["results"] = new JsonArray([.. results.Select(r => (JsonNode)r["resultId"]!.GetValue<string>())]) },
            ["comparability"] = "not_applicable", ["outcome"] = report.Clean ? "ship" : "no_ship", ["exitCode"] = report.Clean ? 0 : 1, ["decidedAt"] = at,
            ["decisive"] = new JsonArray([.. results.Where(r => r["state"]!.GetValue<string>() == "failed").Select(r => (JsonNode)r["resultId"]!.GetValue<string>())]),
        };
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(Path.Combine(folder, "run.json"), run.ToJsonString(), utf8);
        File.WriteAllText(Path.Combine(folder, "results.ndjson"), string.Concat(results.Select(r => r.ToJsonString() + "\n")), utf8);
        File.WriteAllText(Path.Combine(folder, "metrics.json"), metrics.ToJsonString(), utf8);
        File.WriteAllText(Path.Combine(folder, "summary.json"), summary.ToJsonString(), utf8);
        File.WriteAllText(Path.Combine(folder, "gates.ndjson"), gate.ToJsonString() + "\n", utf8);
        return runId;
    }
}
