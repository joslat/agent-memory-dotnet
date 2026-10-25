#pragma warning disable AMEXT001
using AgentMemory.Extensibility.Modules;
using AgentMemory.Tests.Integration.Fixtures;
using AgentMemory.Validation;
using FluentAssertions;
using Xunit.Abstractions;

namespace AgentMemory.Tests.Integration.Validation;

/// <summary>
/// 40.26, the conformance kit on static composition: a module pack's recorded proposals go through the real ingestion
/// pipeline on a real store, and its records, closings, views, time pins and owners are each checked on their own.
/// Broken expectations (mutation controls) must fail the check that names them, which is what makes a pass evidence.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class ModulePackIntegrationTests : IAsyncLifetime
{
    // Each case composes its own module and labels: cleaning the database between cases keeps schema objects, and a
    // module's migration never adopts objects whose ownership it has no record of.
    private static string Module(string @case) => "com.example.pack" + @case.ToLowerInvariant();

    private readonly Neo4jIntegrationFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ModulePackIntegrationTests(Neo4jIntegrationFixture fixture, ITestOutputHelper output) =>
        (_fixture, _output) = (fixture, output);

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static ModuleManifest Manifest(string @case) => ModuleManifest.Parse($$$$"""
        {"format":"agentmemory.module/v1alpha1","id":"{{{{Module(@case)}}}}","version":"1.0.0","sdk":"[0.1,0.2)",
         "ontology":{"labelPrefix":"Pack{{{{@case}}}}","types":[{"name":"Pack{{{{@case}}}}Item","render":"{text}",
           "properties":{"text":{"type":"string","required":true,"maxLength":400}}}]},
         "views":[{"id":"current","type":"Pack{{{{@case}}}}Item","limit":20}]}
        """);

    private static string PackJson(string @case) => """
        {"format":"agentmemory-pack/1","id":"module.lessons","title":"A module's records keep versions, closings, time and owners",
         "owners":["ana","bob"],
         "sessions":[
           {"owner":"ana","id":"ana-1","messages":[
             {"at":"2026-10-20T09:00:00Z","text":"The build fails with MSB3021; a sleep loop did not help."},
             {"at":"2026-10-20T09:05:00Z","role":"assistant","text":"Try building into another artifacts path."}]},
           {"owner":"ana","id":"ana-2","messages":[{"at":"2026-10-21T09:00:00Z","text":"Building into artifacts/alt-build worked."}]},
           {"owner":"bob","id":"bob-1","messages":[{"at":"2026-10-20T10:00:00Z","text":"Our tests need Docker running."}]}],
         "modules":{
           "require":[{"id":"{MODULE}","version":"[1.0.0]"}],
           "teach":[
             {"id":"t1","session":"ana-1","provider":"{MODULE}:writer","proposals":[
               {"type":"PackLessonItem","key":"sleep-loop","properties":{"text":"Retry the build in a sleep loop"},
                "source":{"messageIds":["#0"],"excerpt":"a sleep loop did not help"}}],
              "expect":[{"proposal":0,"status":"Stored"}]},
             {"id":"t2","session":"ana-2","provider":"{MODULE}:writer","existingView":"current","proposals":[
               {"type":"PackLessonItem","key":"artifacts-path","properties":{"text":"Build into artifacts/alt-build when DLLs are locked"},
                "source":{"messageIds":["#0"],"excerpt":"Building into artifacts/alt-build worked."},"replaces":["sleep-loop"]}],
              "closings":[{"proposal":0,"key":"sleep-loop","probability":0.9}],
              "expect":[{"proposal":0,"status":"Stored","closed":["sleep-loop"]}]},
             {"id":"t3","session":"bob-1","provider":"{MODULE}:writer","proposals":[
               {"type":"PackLessonItem","key":"sleep-loop","properties":{"text":"Start Docker before the tests"},
                "source":{"messageIds":["#0"],"excerpt":"Our tests need Docker running."}}],
              "expect":[{"proposal":0,"status":"Stored"}]}],
           "records":[
             {"id":"r1","owner":"ana","module":"{MODULE}","type":"PackLessonItem","key":"sleep-loop","state":"closed"},
             {"id":"r2","owner":"ana","module":"{MODULE}","type":"PackLessonItem","key":"artifacts-path","state":"live","versions":1,
              "properties":{"text":"Build into artifacts/alt-build when DLLs are locked"}},
             {"id":"r3","owner":"bob","module":"{MODULE}","type":"PackLessonItem","key":"sleep-loop","state":"live"},
             {"id":"r4","owner":"bob","module":"{MODULE}","type":"PackLessonItem","key":"artifacts-path","state":"absent"}],
           "views":[
             {"id":"v1","owner":"ana","module":"{MODULE}","view":"current","expectKeys":["artifacts-path"],
              "requiredText":["alt-build"],"excludedText":["Docker","sleep loop"]},
             {"id":"v2","owner":"bob","module":"{MODULE}","view":"current","expectKeys":["sleep-loop"],
              "requiredText":["Docker"],"excludedText":["alt-build"]},
             {"id":"v3","owner":"ana","module":"{MODULE}","view":"current","systemAsOf":"2026-10-20T12:00:00Z",
              "expectKeys":["sleep-loop"],"requiredText":["sleep loop"]}]}}
        """.Replace("{MODULE}", Module(@case), StringComparison.Ordinal).Replace("PackLessonItem", "Pack" + @case + "Item", StringComparison.Ordinal);

    [Fact]
    public async Task A_module_pack_passes_every_check_through_the_real_pipeline()
    {
        var pack = ValidationPackReader.Parse(PackJson("Base"));
        ValidationPackReader.Check(pack).Should().BeEmpty();

        var result = await Runner("Base").RunAsync(pack);
        foreach (var check in result.Checks) _output.WriteLine($"{(check.Passed ? "pass" : "FAIL")} {check.Id}: {check.Detail}");

        result.Failures.Select(f => $"{f.Id}: {f.Detail}").Should().BeEmpty();
        result.Checks.Select(c => c.Kind).Should().Contain(["modules", "teach", "records", "views", "isolation"]);
        result.Checks.Count(c => c.Kind is "teach" or "records" or "views").Should().BeGreaterThanOrEqualTo(14);
    }

    public static TheoryData<string, string> Mutations() => new()
    {
        { "wrong-expected-key", "views:v1:keys" },
        { "missing-teach-step", "records:r1,records:r2" },
        { "judge-declines", "teach:t2:0,records:r1" },
        { "excluded-text-rendered", "views:v1:excluded:1" },
        { "wrong-property", "records:r2" },
        { "no-composition", "modules:composition" },
    };

    /// <summary>Mutation controls: each broken expectation or input fails exactly the checks that name it.</summary>
    [Theory]
    [MemberData(nameof(Mutations))]
    public async Task A_broken_pack_fails_the_checks_that_name_what_broke(string mutation, string failing)
    {
        var @case = new string([.. mutation.Split('-').Select(w => char.ToUpperInvariant(w[0]))]);
        var pack = ValidationPackReader.Parse(PackJson(@case));
        var modules = pack.Modules!;
        pack = mutation switch
        {
            "wrong-expected-key" => pack with { Modules = modules with { Views = [modules.Views[0] with { ExpectKeys = ["sleep-loop"] }, .. modules.Views.Skip(1)] } },
            "missing-teach-step" => pack with { Modules = modules with { Teach = [modules.Teach[0], modules.Teach[2]] } },
            "judge-declines" => pack with { Modules = modules with { Teach = [modules.Teach[0],
                modules.Teach[1] with { Closings = [modules.Teach[1].Closings[0] with { Probability = 0.1 }] }, modules.Teach[2]] } },
            "excluded-text-rendered" => pack with { Modules = modules with { Views = [modules.Views[0] with { ExcludedText = ["alt-build"] }, .. modules.Views.Skip(1)] } },
            "wrong-property" => pack with { Modules = modules with { Records = [modules.Records[0], modules.Records[1] with
                { Properties = new Dictionary<string, System.Text.Json.JsonElement> { ["text"] = System.Text.Json.JsonSerializer.SerializeToElement("other") } },
                .. modules.Records.Skip(2)] } },
            _ => pack,
        };

        var result = await Runner(@case, composed: mutation != "no-composition").RunAsync(pack);
        foreach (var check in result.Failures) _output.WriteLine($"FAIL {check.Id}: {check.Detail}");

        result.Passed.Should().BeFalse();
        result.Failures.Select(f => f.Id).Should().Contain(failing.Split(','));
    }

    private ValidationPackRunner Runner(string @case, bool composed = true) => new(o =>
    {
        o.Uri = _fixture.ConnectionString;
        o.Username = _fixture.User;
        o.Password = _fixture.Password;
        o.Database = "neo4j";
        o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
    })
    {
        Modules = composed ? modules => modules.Add(Manifest(@case), _ => { }) : null,
    };
}
