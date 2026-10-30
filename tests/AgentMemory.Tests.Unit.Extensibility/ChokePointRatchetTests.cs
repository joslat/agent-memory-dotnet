using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The guard for root RP, "a rule lives at the door where it was found" (review 7's root-cause analysis): pack code reads a file,
/// parses JSON or writes to the raw console only through its choke points, and every other site is listed here with its reason.
/// A new site fails this test until it is listed, so a new door cannot skip the bound or the authentication rule unseen; a site
/// that goes away must leave the list too, so the list only shrinks. Console writes in the tool are not listed: <c>Main</c>
/// installs the one escaping writer (<c>ShownWriter</c>) on both streams, and only replacing the streams is a raw site.
/// </summary>
public sealed class ChokePointRatchetTests
{
    private static readonly string[] Scopes =
    [
        "src/AgentMemory.Packs.", "src/AgentMemory.Extensibility.Ampack/", "src/AgentMemory.Extensibility/Packs/",
        "src/AgentMemory.Extensibility/Modules/", "src/AgentMemory.Extensibility/Bundles/", "tools/Ampack/", "shared/",
    ];

    private static readonly (string Api, Regex Pattern)[] Apis =
    [
        ("file read", new(@"\bFile\.(ReadAll(Bytes|Text|Lines)(Async)?|Open|OpenRead|OpenText)\s*\(|\bnew\s+FileStream\s*\(")),
        ("json parse", new(@"\bJsonDocument\.Parse(Async)?\s*\(|\bJsonNode\.Parse\s*\(|\bJsonSerializer\.Deserialize(Async)?\b")),
        ("raw console", new(@"\bConsole\.(OpenStandard\w*|SetOut|SetError)\s*\(")),
        // Review 8, F3: one pack's own signatures, not a set's members. A door that acts on a pack takes VerifyWhole.
        ("one-pack verify", new(@"\bPackVerifier\.VerifyOne\s*\(")),
    ];

    private const string Parser = "a parser of the format, which bounds its input or is reached only with bytes the format bounds";
    private const string Authoring = "authoring: reads the author's own sources before any pack exists";
    private const string Verified = "reads content only after the pack is authenticated, or bytes already bounded by its caller";

    // (file, api, sites, reason). Counts are exact: add a site and this fails until it is listed, remove one and it fails until
    // the count drops.
    private static readonly (string File, string Api, int Sites, string Reason)[] Allowed =
    [
        ("shared/BoundedFile.cs", "file read", 1, "the choke point: the one bounded way pack code reads a file"),
        ("src/AgentMemory.Packs.Format/HostLedger.cs", "file read", 1, "the ledger's own lock file, opened to hold it, never read"),
        ("src/AgentMemory.Extensibility.Ampack/PackStore.cs", "file read", 1, "the store's own lock file, opened to hold it, never read"),
        ("tools/Ampack/Program.cs", "file read", 1, "the output file a command writes"),
        ("src/AgentMemory.Extensibility/Modules/ModuleAssemblyReader.cs", "file read", 1, "a module assembly the host was configured with"),
        ("src/AgentMemory.Packs.CodeMap/Scip/ScipCodeMapReader.cs", "file read", 1, Authoring),
        ("src/AgentMemory.Packs.CodeMap.Roslyn/RoslynCodeMapReader.cs", "file read", 2, Authoring),
        ("src/AgentMemory.Packs.Architecture/ArchitectureReader.cs", "file read", 1, Authoring),
        ("src/AgentMemory.Packs.Decisions/DecisionsReader.cs", "file read", 2, Authoring),
        ("src/AgentMemory.Packs.Intent/IntentReader.cs", "file read", 1, Authoring),
        ("src/AgentMemory.Extensibility/Packs/ModuleProjection.cs", "file read", 1, "authoring: the projection file it is about to rewrite"),

        ("src/AgentMemory.Packs.Format/Dsse.cs", "json parse", 2, Parser),
        ("src/AgentMemory.Packs.Format/FilesList.cs", "json parse", 1, Parser),
        ("src/AgentMemory.Packs.Format/HostLedger.cs", "json parse", 1, Parser),
        ("src/AgentMemory.Packs.Format/Jcs.cs", "json parse", 1, Parser),
        ("src/AgentMemory.Packs.Format/PackCard.cs", "json parse", 1, Parser),
        ("src/AgentMemory.Packs.Format/PackCertificate.cs", "json parse", 4, Parser),
        ("src/AgentMemory.Packs.Format/PackFamily.cs", "json parse", 1, Parser),
        ("src/AgentMemory.Packs.Format/PackJson.cs", "json parse", 1, "the choke point: the format's JSON profile check"),
        ("src/AgentMemory.Packs.Format/PackManifest.cs", "json parse", 1, Parser),
        ("src/AgentMemory.Packs.Format/PackVerifier.cs", "json parse", 1, Parser),
        ("src/AgentMemory.Packs.Format/PublicationReceipt.cs", "json parse", 2, Parser),
        ("src/AgentMemory.Packs.Format/Revocations.cs", "json parse", 2, Parser),
        ("src/AgentMemory.Packs.Format/TrustPolicy.cs", "json parse", 1, Parser),
        ("src/AgentMemory.Packs.Format/PackLayout.cs", "json parse", 1, Authoring),
        ("src/AgentMemory.Extensibility.Ampack/PackCertificates.cs", "json parse", 1, "an untrusted certificate's claims, after its size and JSON profile are checked"),
        ("src/AgentMemory.Extensibility.Ampack/PackDiff.cs", "json parse", 2, "bounded by PackContentBounds when the pack is not verified (review 7, D2)"),
        ("src/AgentMemory.Extensibility.Ampack/PackScan.cs", "json parse", 1, "bounded by PackContentBounds when the pack is not verified (review 7, D2)"),
        ("src/AgentMemory.Extensibility.Ampack/PackStore.cs", "json parse", 1, "the store's own index, read bounded"),
        ("src/AgentMemory.Extensibility.Ampack/PackTests.cs", "json parse", 1, Verified),
        ("src/AgentMemory.Extensibility/Bundles/BundleVerifier.cs", "json parse", 2, "the legacy bundle's signature and list, read bounded"),
        ("src/AgentMemory.Extensibility/Modules/CanonicalJson.cs", "json parse", 1, "canonicalizes JSON this library serialized itself"),
        ("src/AgentMemory.Extensibility/Modules/ModuleJson.cs", "json parse", 1, "the module manifest parser, depth-bounded"),
        ("src/AgentMemory.Extensibility/Modules/PackActivation.cs", "json parse", 3, "the host's own decision records, read bounded"),
        ("src/AgentMemory.Extensibility/Packs/KnowledgePack.cs", "json parse", 1, Verified),
        ("src/AgentMemory.Extensibility/Packs/ModuleProposer.cs", "json parse", 1, "authoring: a model's reply, which yields a proposal, not a pack"),
        ("src/AgentMemory.Extensibility/Packs/ModuleReview.cs", "json parse", 2, "authoring: a review set the author edits"),
        ("src/AgentMemory.Packs.Repository/RepositoryWriter.cs", "json parse", 1, "authoring: a model's reply, depth-bounded"),
        ("src/AgentMemory.Packs.Solid/SolidPacks.cs", "json parse", 1, "the publisher's own pack.json, in the pack it is publishing"),
        ("src/AgentMemory.Packs.Solid/SolidSession.cs", "json parse", 2, "a Solid token response, read bounded by the session"),
        ("src/AgentMemory.Packs.Solid/WebIdProfile.cs", "json parse", 1, "a key from a WebID profile, from a document the session read bounded"),
        ("src/AgentMemory.Packs.Work/GitHubWorkReader.cs", "json parse", 2, Authoring),
        ("src/AgentMemory.Packs.Work/WorkLedgerReader.cs", "json parse", 1, Authoring),
        ("src/AgentMemory.Packs.CodeMap.Roslyn/RoslynCodeMapReader.cs", "json parse", 1, Authoring),
        ("src/AgentMemory.Packs.Architecture/ArchitectureReader.cs", "json parse", 1, Authoring),
        ("tools/Ampack/Program.cs", "json parse", 2, "a pack.json the tool just wrote, and the trust policy, read bounded"),
        ("tools/Ampack/Program.Solid.cs", "json parse", 1, "the trust policy, read bounded"),
        ("tools/Ampack/Rest.cs", "json parse", 1, "a request body, read bounded by the server"),

        ("src/AgentMemory.Extensibility.Ampack/PackFamilies.cs", "one-pack verify", 1, "a composite opened as one pack; a set is refused there and each pin verified"),
        ("src/AgentMemory.Extensibility.Ampack/PackInspection.cs", "one-pack verify", 1, "reports each step; a set is then verified whole before it shows as loaded"),
        ("src/AgentMemory.Extensibility.Ampack/PackLoader.cs", "one-pack verify", 1, "the one-module loader, which refuses a set (open its members)"),
        ("tools/Ampack/Program.cs", "one-pack verify", 1, "ampack verify, which reports a set member by member through VerifySet"),

        ("tools/Ampack/Program.cs", "raw console", 3, "Main installs the escaping writer; sign-bytes reads standard input, bounded"),
    ];

    [Fact]
    public void Pack_code_reads_parses_and_prints_only_through_its_choke_points_or_a_listed_site()
    {
        var root = Root();
        var found = new Dictionary<(string File, string Api), int>();
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var file = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!Scopes.Any(s => file.StartsWith(s, StringComparison.Ordinal)) || file.Contains("/bin/", StringComparison.Ordinal) || file.Contains("/obj/", StringComparison.Ordinal))
                continue;
            foreach (var line in File.ReadLines(path))
            {
                var code = line.Split("//", 2)[0];
                foreach (var (api, pattern) in Apis)
                    if (pattern.Matches(code).Count is > 0 and var n)
                        found[(file, api)] = found.GetValueOrDefault((file, api)) + n;
            }
        }

        var allowed = Allowed.ToDictionary(a => (a.File, a.Api), a => a.Sites);
        var unlisted = found.Where(f => !allowed.TryGetValue(f.Key, out var k) || f.Value > k)
            .Select(f => $"{f.Key.File}: {f.Value} {f.Key.Api} site(s), {allowed.GetValueOrDefault(f.Key)} listed").ToList();
        var stale = allowed.Where(a => found.GetValueOrDefault(a.Key) < a.Value)
            .Select(a => $"{a.Key.File}: {a.Value} {a.Key.Api} site(s) listed, {found.GetValueOrDefault(a.Key)} found").ToList();

        unlisted.Should().BeEmpty("a new raw site goes through its choke point (BoundedFile, PackJson, the verified pack, ShownWriter), or is listed here with its reason");
        stale.Should().BeEmpty("a site that went away leaves the list, so the list only shrinks");
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root holds AgentMemory.slnx.");
    }
}
