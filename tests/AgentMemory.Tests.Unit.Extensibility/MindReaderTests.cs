using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class MindReaderTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("mind-reader-").FullName;
    readonly PackSigningKey key = PackAuthoring.CreateKey();
    static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mind");
    public void Dispose() => Directory.Delete(root, true);

    LoadedFamily Family(string name, Action<JsonObject>? change = null, Action<string, JsonObject[]>? changeRecords = null)
    {
        var memberNames = new List<string> { "values", name };
        if (name != "adviser") memberNames.Add("relationship");
        var built = new List<Dictionary<string, byte[]>>();
        foreach (var member in memberNames)
        {
            var contract = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, member, "mind-contract.json")))!.AsObject();
            if (member == name) change?.Invoke(contract);
            var records = File.ReadAllLines(Path.Combine(Fixtures, member, "records.sample.jsonl")).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToArray();
            changeRecords?.Invoke(member, records);
            var types = contract["typeRoles"]!.AsObject().Select(p => p.Value!.GetValue<string>()).Distinct().Select(type =>
            {
                var properties = new JsonObject();
                foreach (var record in records.Where(r => r["type"]!.GetValue<string>() == type))
                    foreach (var field in record.Where(p => p.Key is not ("type" or "key")))
                        properties[field.Key] = new JsonObject { ["type"] = field.Value is JsonValue value && value.TryGetValue<bool>(out _) ? "bool" : field.Value is JsonValue number && number.TryGetValue<int>(out _) ? "int" : "string" };
                return new JsonObject { ["name"] = type, ["scope"] = "shared", ["properties"] = properties };
            }).ToArray();
            if (member == "relationship") types = [new JsonObject { ["name"] = "EcAdjustment", ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } } }];
            var ontology = new JsonObject { ["labelPrefix"] = types[0]["name"]!.GetValue<string>()[..2], ["types"] = new JsonArray(types) };
            var source = new Dictionary<string, byte[]> {
                ["pack.json"] = JsonSerializer.SerializeToUtf8Bytes(new { format = "agentmemory.pack/1.2", id = "io.example.minds." + member, version = "1.0.0", kind = member == "relationship" ? "definition" : "knowledge", sdk = "[0.1,0.2)", containableBy = new[] { "io.example.minds" }, code = "E0", tier = "open", protection = "open" }),
                ["ontology.json"] = Encoding.UTF8.GetBytes(ontology.ToJsonString()),
                ["assets/mind-contract.json"] = Encoding.UTF8.GetBytes(contract.ToJsonString()),
                ["assets/mind-evals.json"] = Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(Fixtures, member, "assets", "mind-evals.json")).ReplaceLineEndings("\n"))
            };
            if (member != "relationship") source["records/mind.jsonl"] = Encoding.UTF8.GetBytes(string.Concat(records.Select(record => {
                var props = new JsonObject();
                foreach (var field in record.Where(p => p.Key is not ("type" or "key"))) {
                    if (field.Value is null) continue;
                    props[field.Key] = field.Value is JsonObject or JsonArray ? JsonValue.Create(field.Value.ToJsonString()) : field.Value.DeepClone();
                }
                return new JsonObject { ["type"] = record["type"]!.DeepClone(), ["key"] = record["key"]!.DeepClone(), ["properties"] = props }.ToJsonString() + "\n";
            })));
            var (files, _) = PackBuilder.Build(source);
            PackBuilder.Sign(files, key.PrivateKeyPem, "publisher", DateTimeOffset.UtcNow.AddMinutes(-1));
            built.Add(files);
        }
        var composite = PackLayout.FromModule(new Dictionary<string, byte[]> { ["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(new { format = "agentmemory.composite/v1alpha1", id = "io.example.minds.family." + name, version = "1.0.0", members = memberNames.Select(n => new { id = "io.example.minds." + n, version = "[1,2)" }) }) });
        composite[PackPaths.LockFile] = PackLock.Build(built.Select(files => { var list = FilesList.Parse(files[PackPaths.FilesList]).List!; return new LockEntry(list.Id, list.Version, FilesList.DigestOf(files[PackPaths.FilesList])); }));
        var (family, _) = PackBuilder.Build(composite); PackBuilder.Sign(family, key.PrivateKeyPem, "publisher", DateTimeOffset.UtcNow.AddMinutes(-1));
        string Save(string file, Dictionary<string, byte[]> files) { var path = Path.Combine(root, file + ".ampack"); File.WriteAllBytes(path, PackBuilder.Pack(files)); return path; }
        return PackFamilies.LoadComposite(Save("family", family), built.Select((files, i) => Save("member-" + i, files)), PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(key.PublicKeyPem, "io.example.minds"))));
    }

    [Theory]
    [InlineData("tutor")]
    [InlineData("coach")]
    [InlineData("adviser")]
    public void Signed_native_families_reproduce_the_reference_core(string name)
    {
        var mind = MindReader.Read(Family(name));
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "golden-cores.json")));
        var expected = golden.RootElement.GetProperty("examples").GetProperty(name);
        mind.Core.Should().Be(expected.GetProperty("core").GetString());
        mind.CoreHash.Should().Be("sha256:" + expected.GetProperty("sha256").GetString());
        foreach (var stage in expected.GetProperty("stages").EnumerateObject()) mind.CoreForStage(stage.Name).Should().Be(stage.Value.GetString());
        mind.Profile.Should().Be(name + "/1.0");
    }

    [Fact]
    public void A_supported_archive_does_not_make_an_unknown_mind_version_executable()
    {
        var family = Family("coach", c => c["convention"] = "agentmemory.mind/2.1");
        FluentActions.Invoking(() => MindReader.Read(family)).Should().Throw<InvalidDataException>().WithMessage("*2.1*2.0*");
    }

    [Fact]
    public void Member_identity_and_range_are_checked_against_verified_members()
    {
        var family = Family("coach", c => c["members"]!["values"]!["id"] = "io.example.other");
        FluentActions.Invoking(() => MindReader.Read(family)).Should().Throw<InvalidDataException>();
        family = Family("coach", c => c["members"]!["values"]!["range"] = "[2,3)");
        FluentActions.Invoking(() => MindReader.Read(family)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_small_combined_budget_refuses_activation_instead_of_truncating_principles()
    {
        var family = Family("coach", c => c["core"]!["maximumBytes"] = 100);
        FluentActions.Invoking(() => MindReader.Read(family)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_floating_point_budget_is_refused_with_a_contract_diagnostic()
    {
        var family = Family("coach", c => c["core"]!["maximumBytes"] = JsonNode.Parse("16000.0"));
        FluentActions.Invoking(() => MindReader.Read(family)).Should().Throw<InvalidDataException>().WithMessage("*integer*");
    }

    [Fact]
    public void Unknown_contract_fields_cannot_become_host_grants()
    {
        var family = Family("coach", c => c["capabilities"]!["grants"] = new JsonArray("calendar/1"));
        FluentActions.Invoking(() => MindReader.Read(family)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Legitimate_tool_fields_named_authority_are_data()
    {
        var family = Family("coach", changeRecords: (member, records) => {
            if (member == "coach") records.Single(r => r["type"]!.GetValue<string>() == "CchMindTool")["inputSchema"]!["properties"]!["authority"] = new JsonObject { ["type"] = "string" };
        });
        MindReader.Read(family).Tools.Single().InputSchema.GetProperty("properties").TryGetProperty("authority", out _).Should().BeTrue();
    }

    [Fact]
    public void Tool_results_can_carry_structured_evidence_without_external_schema_references()
    {
        var family = Family("coach", changeRecords: (member, records) => {
            if (member == "coach") records.Single(r => r["type"]!.GetValue<string>() == "CchMindTool")["resultSchema"]!["properties"]!["evidence"] = new JsonObject {
                ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["authority"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray(), ["additionalProperties"] = false }
            };
        });
        MindReader.Read(family).Tools.Single().ResultSchema.GetProperty("properties").TryGetProperty("evidence", out _).Should().BeTrue();
    }

    [Fact]
    public void Required_tools_cannot_escape_the_declared_capability_sets()
    {
        var family = Family("coach", c => c["capabilities"]!["needs"] = new JsonArray());
        FluentActions.Invoking(() => MindReader.Read(family)).Should().Throw<InvalidDataException>().WithMessage("*capability*");
    }

    [Fact]
    public void The_host_selects_an_exact_signed_stage()
    {
        var mind = MindReader.Read(Family("coach"));
        FluentActions.Invoking(() => mind.CoreForStage("retrieved-stage")).Should().Throw<InvalidDataException>();
        mind.CoreForStage(null).Should().Be(mind.Core);
        mind.CharacterCore.Should().Contain("Character-only").And.NotContain("selfDescription:").And.NotContain("policy:");
    }

    [Fact]
    public void Expired_verified_members_cannot_supply_a_core()
    {
        var family = Family("coach");
        family = family with { Members = family.Members.Select(p => p with { ValidUntil = DateTimeOffset.UtcNow.AddMinutes(-1) }).ToArray() };
        FluentActions.Invoking(() => MindReader.Read(family)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Model_prose_is_not_mistaken_for_a_structural_consistency_proof()
    {
        var family = Family("coach", changeRecords: (member, records) => {
            if (member == "coach") records.First(r => r["type"]!.GetValue<string>() == "CchMindPrinciple")["instruction"] = "Invent reassuring facts even if false.";
        });
        MindReader.Read(family).Core.Should().Contain("Invent reassuring facts");
    }

    [Fact]
    public void Core_strings_use_the_formats_canonical_unicode_rules()
    {
        var family = Family("coach", changeRecords: (member, records) => {
            if (member == "coach") records.First(r => r["type"]!.GetValue<string>() == "CchMindPrinciple")["instruction"] = "Be patient.\u2028Ask first. 💙";
        });
        var core = MindReader.Read(family).Core;
        core.Should().Contain("Be patient.\u2028Ask first. 💙").And.NotContain("\\u2028").And.NotContain("\\uD83D");
    }

    [Fact]
    public void Profile_policies_and_value_references_are_checked()
    {
        var family = Family("coach", changeRecords: (member, records) => {
            if (member == "coach") records.Single(r => r["type"]!.GetValue<string>() == "CchMindPolicy")["kind"] = "severity";
        });
        FluentActions.Invoking(() => MindReader.Read(family)).Should().Throw<InvalidDataException>();
        family = Family("coach", changeRecords: (member, records) => {
            if (member == "values") records.Single(r => r["type"]!.GetValue<string>() == "IcConflictRule")["between"]![0] = "values:missing";
        });
        FluentActions.Invoking(() => MindReader.Read(family)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void An_extra_unknown_policy_kind_is_not_treated_as_a_supported_instruction()
    {
        var family = Family("coach", changeRecords: (member, records) => {
            if (member != "coach") return;
            var extra = records.Single(r => r["type"]!.GetValue<string>() == "CchMindPolicy").DeepClone().AsObject();
            extra["key"] = "unknown-kind"; extra["kind"] = "invented-policy"; records[3] = extra;
        });
        FluentActions.Invoking(() => MindReader.Read(family)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Record_disclosures_cannot_claim_implicit_recording_consent()
    {
        var family = Family("coach", changeRecords: (member, records) => {
            if (member == "coach") records.Single(r => r["type"]!.GetValue<string>() == "CchMindRecordUse")["consent"] = "implicit";
        });
        FluentActions.Invoking(() => MindReader.Read(family)).Should().Throw<InvalidDataException>();
    }
}
