// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;

namespace Ampack;

/// <summary>
/// <c>--json</c> (DIST-2): a command answers with one JSON document on standard output, the same shape for every command that
/// takes it, so a wrapper in another language and the hub's checks read results without parsing text:
/// <c>{"format":"ampack.result/1","command","ok","exitCode","problems":[…],"result":{…}}</c>. A refusal and a usage error come
/// the same way, with their problems and no result; <c>ok</c> is whether the exit code is 0, as the text output's exit code says.
/// The document is written with nodes, never by reflection, so the native build stays possible (DIST-1), and through the same
/// escaping writer as every other line.
/// </summary>
internal static partial class Program
{
    /// <summary>The format of every <c>--json</c> answer.</summary>
    internal const string ResultFormat = "ampack.result/1";

    // The command answering in JSON, or null when it prints text: set once Main has checked the command takes --json.
    private static string? _jsonCommand;

    private static bool Json => _jsonCommand is not null;

    // The one JSON answer of a command: its exit code, its problems (as the text output shows them) and its result.
    private static int Answer(int exitCode, IEnumerable<string> problems, JsonNode? result)
    {
        var document = new JsonObject
        {
            ["format"] = ResultFormat,
            ["command"] = _jsonCommand,
            ["ok"] = exitCode == 0,
            ["exitCode"] = exitCode,
            ["problems"] = new JsonArray([.. problems.Select(p => (JsonNode?)Shown(p, 4000))]),
            ["result"] = result,
        };
        Console.Write(Pretty(document));
        return exitCode;
    }

    private static JsonArray JsonList(IEnumerable<JsonNode?> items) => new([.. items]);

    private static JsonArray JsonStrings(IEnumerable<string> items) => new([.. items.Select(s => (JsonNode?)s)]);

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // A verified pack as verify shows it: identity, kind, tier, the profiles it uses (and whether this reader implements each),
    // its signatures, and its publisher when it is federated.
    private static JsonObject VerifiedJson(VerifiedPack pack) => new()
    {
        ["id"] = pack.Id,
        ["version"] = pack.Version,
        ["digest"] = pack.Digest,
        ["kind"] = pack.Manifest.Kind,
        ["tier"] = pack.Manifest.Tier,
        ["profiles"] = JsonList(PackProfiles.Used(pack.Files.Keys, pack.Manifest).Select(p => (JsonNode?)new JsonObject { ["name"] = p.Name, ["implemented"] = p.Implemented })),
        ["signatures"] = JsonList(pack.Signatures.Select(s => (JsonNode?)new JsonObject { ["role"] = s.Role, ["keyId"] = s.KeyId, ["signedAt"] = Utc(s.SignedAt) })),
        ["publisher"] = pack.Manifest.Federation?.Publisher,
    };

    // What inspect reads, none of it verified: the files list's identity and files, the format it declares, its signatures and
    // certificates.
    private static JsonObject InspectJson(PackContent content)
    {
        var result = new JsonObject { ["source"] = content.Source, ["verified"] = false };
        if (content.Files.TryGetValue(PackPaths.FilesList, out var listBytes) && FilesList.Parse(listBytes) is ({ } list, _))
        {
            var manifest = content.Files.TryGetValue(PackManifest.FileName, out var manifestBytes) ? PackManifest.Parse(manifestBytes).Manifest : null;
            result["pack"] = new JsonObject
            {
                ["id"] = list.Id, ["version"] = list.Version, ["digest"] = FilesList.DigestOf(listBytes),
                ["formatMinor"] = manifest?.FormatMinor, ["publisher"] = manifest?.Federation?.Publisher,
            };
            result["files"] = JsonList(list.Files.Select(f => (JsonNode?)new JsonObject { ["path"] = f.Path, ["length"] = f.Length }));
        }
        else result["pack"] = null;
        result["signatures"] = JsonList(content.Files.Keys.Where(PackPaths.IsSignature).Order(StringComparer.Ordinal)
            .Select(path => (JsonNode?)new JsonObject { ["path"] = path, ["kind"] = PackPaths.IsTimestamp(path) ? "timestamp" : "envelope" }));
        result["certificates"] = JsonStrings(PackAttestations.Certificates(content.Files).Select(c => PackAttestations.CertificatePath(c.Stem)));
        return result;
    }

    // What scan found, and the AEF run it wrote when asked; the exit code is scan's: 1 when anything was found.
    private static int ScanJson(Options o, PackContent content, AgentMemory.Extensibility.Ampack.PackScanReport report)
    {
        var result = new JsonObject
        {
            ["clean"] = report.Clean, ["files"] = report.Files, ["strings"] = report.Strings,
            ["findings"] = JsonList(report.Findings.Select(f => (JsonNode?)new JsonObject
            {
                ["severity"] = f.Severity, ["rule"] = f.Rule, ["category"] = f.Category, ["file"] = f.File, ["line"] = f.Line, ["path"] = f.Path, ["sample"] = f.Sample,
            })),
        };
        if (o.Value("aef") is { } folder)
        {
            if (!content.Files.TryGetValue(PackPaths.FilesList, out var listBytes) || FilesList.Parse(listBytes).List is not { } list)
                return Report(["An AEF run names the pack by its digest: build the pack first."], "");
            result["run"] = new JsonObject
            {
                ["id"] = AgentMemory.Extensibility.Ampack.PackScan.WriteRun(report, list.Id, list.Version, FilesList.DigestOf(listBytes), DateTimeOffset.UtcNow, folder,
                    typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "2.0.0"),
                ["folder"] = folder,
            };
        }
        return Answer(report.Clean ? 0 : 1, [], result);
    }

    private static JsonObject InstalledJson(AgentMemory.Extensibility.Ampack.InstalledPack p) => new()
    {
        ["id"] = p.Id, ["version"] = p.Version, ["kind"] = p.Kind, ["digest"] = p.Digest, ["file"] = p.File, ["installedAt"] = Utc(p.InstalledAt),
        ["members"] = JsonStrings(p.Members),
    };
}
