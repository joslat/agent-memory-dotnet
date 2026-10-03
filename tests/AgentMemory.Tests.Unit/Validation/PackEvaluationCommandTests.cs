using AgentMemory.Cli.Commands;
using AgentMemory.Validation;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Validation;

/// <summary>40.57: <c>evaluate --pack</c> finds the core packs, a file, or every pack in a directory.</summary>
public sealed class PackEvaluationCommandTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("packs-").FullName;

    [Fact]
    public void Core_names_the_packs_shipped_with_the_library()
    {
        PackEvaluationCommand.Load("core").Select(p => p.Id).Should().Equal(ValidationPackReader.Core().Select(p => p.Id));
        PackEvaluationCommand.Load(null).Should().HaveSameCount(ValidationPackReader.Core());
    }

    [Fact]
    public void A_directory_runs_every_pack_in_it_in_name_order()
    {
        File.WriteAllText(Path.Combine(_directory, "b.json"), Pack("test.b"));
        File.WriteAllText(Path.Combine(_directory, "a.json"), Pack("test.a"));
        File.WriteAllText(Path.Combine(_directory, "notes.txt"), "not a pack");

        PackEvaluationCommand.Load(_directory).Select(p => p.Id).Should().Equal(["test.a", "test.b"]);
        PackEvaluationCommand.Load(Path.Combine(_directory, "b.json")).Single().Id.Should().Be("test.b");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Pack(string id) =>
        $$"""{ "format": "agentmemory-pack/1", "id": "{{id}}", "title": "t", "owners": ["a", "b"] }""";
}
