using System.Text.Json.Nodes;
using AgentMemory.RouterArena.Ask;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Gate;

public sealed class CompoundEvaluationCommandTests
{
    [Fact]
    public async Task Dry_run_validates_and_counts_without_credentials_or_output_files()
    {
        using var files = new Files();
        using var output = new StringWriter();

        var result = await CompoundEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, true, null, output);

        result.Should().Be(0);
        output.ToString().Should().Contain("1 cases").And.Contain("Nothing was sent");
        File.Exists(files.Prefix + ".baseline.json").Should().BeFalse();
        File.Exists(files.Prefix + ".compound.json").Should().BeFalse();
    }

    [Fact]
    public async Task Existing_evidence_is_never_overwritten()
    {
        using var files = new Files();
        await File.WriteAllTextAsync(files.Prefix + ".baseline.json", "keep this evidence");

        var result = await CompoundEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, false, null, TextWriter.Null);

        result.Should().Be(1);
        (await File.ReadAllTextAsync(files.Prefix + ".baseline.json")).Should().Be("keep this evidence");
    }

    [Fact]
    public async Task An_existing_run_reservation_rejects_reuse_of_its_prefix()
    {
        using var files = new Files();
        await File.WriteAllTextAsync(files.Prefix + ".lock", "reserved");

        var result = await CompoundEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, true, null, TextWriter.Null);

        result.Should().Be(1);
    }

    [Theory]
    [InlineData("{\"threshold\":2}")]
    [InlineData("{\"timeout\":\"00:00:00\"}")]
    [InlineData("{\"examplesPath\":\"unhashed-input.json\"}")]
    [InlineData("{\"judges\":[null]}")]
    [InlineData("{\"compoundDetector\":{\"endpoint\":\"ftp://judge.test/invalid\"}}")]
    public async Task Invalid_or_unbound_configuration_is_rejected_before_provider_work(string config)
    {
        using var files = new Files();
        var valid = JsonNode.Parse(await File.ReadAllTextAsync(files.Config))!.AsObject();
        foreach (var property in JsonNode.Parse(config)!.AsObject()) valid[property.Key] = property.Value?.DeepClone();
        await File.WriteAllTextAsync(files.Config, valid.ToJsonString());

        var result = await CompoundEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, true, null, TextWriter.Null);

        result.Should().Be(1);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"id\":\"bad\",\"question\":\"test\",\"candidates\":[null]}")]
    public async Task Malformed_case_shapes_return_an_input_error(string item)
    {
        using var files = new Files();
        var data = JsonNode.Parse(await File.ReadAllTextAsync(files.Data))!;
        data["cases"] = new JsonArray(JsonNode.Parse(item));
        await File.WriteAllTextAsync(files.Data, data.ToJsonString());

        var result = await CompoundEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, true, null, TextWriter.Null);

        result.Should().Be(1);
    }

    private sealed class Files : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "compound-test-" + Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(_directory, "data.json");
        public string Config => Path.Combine(_directory, "config.json");
        public string Prefix => Path.Combine(_directory, "result");
        public Files()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Data, """
                {"schemaVersion":1,"status":"development","now":"2026-09-01T00:00:00Z","cases":[
                  {"id":"one","question":"Who owns the task?","candidates":[
                    {"key":"a","memoryType":"semantic","text":"A owns the task"}],"requiredGroups":[["a"]]}
                ]}
                """);
            File.WriteAllText(Config, """
                {"judges":[{"name":"fake","endpoint":"https://judge.test/v1/systemone"}],
                 "compoundDetector":{"name":"fake","endpoint":"https://judge.test/v1/systemone"}}
                """);
        }
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
    }
}
