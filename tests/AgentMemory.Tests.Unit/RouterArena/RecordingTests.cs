using AgentMemory.Abstractions.Domain;
using AgentMemory.RouterArena.Data;
using AgentMemory.RouterArena.Record;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.RouterArena;

/// <summary>40.71: the router arena's data: a matrix read with its split, a recall turned into keyed hits, the world's ids.</summary>
public sealed class RecordingTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-arena-").FullName;

    [Fact]
    public void A_matrix_is_read_with_each_turn_the_turns_before_it_and_its_split()
    {
        var path = Path.Combine(_directory, "matrix.json");
        File.WriteAllText(path, """
            {"format":"routing-matrix/1","items":[
              {"id":"m:a:01","text":"When is my dentist?","prior":[],"needs":[["F17"]],"acceptable":["E16"],"split":"heldout"},
              {"id":"m:b:01","text":"and his kids?","prior":[{"role":"user","text":"What does Iker do?"}],"needs":[["R05","E10"],["R06","E11"]]},
              {"id":"m:c:01","text":"hi"}
            ]}
            """);

        var matrix = Matrix.Read(path);

        matrix.Items.Select(t => t.Id).Should().Equal("m:a:01", "m:b:01", "m:c:01");
        matrix.Items[1].Prior.Should().ContainSingle().Which.Should().Be(new PriorTurn("user", "What does Iker do?"));
        matrix.Items[1].Needs.Should().HaveCount(2, "two groups: each kid");
        Matrix.SplitOf(matrix.Items[0]).Should().Be("heldout", "a frozen split is kept as written");
        Matrix.SplitOf(matrix.Items[2]).Should().BeOneOf("dev", "heldout");
        matrix.Sha256.Should().HaveLength(64);
    }

    [Fact]
    public void A_recall_becomes_hits_keyed_like_the_world_with_scores_ranks_and_owners()
    {
        var context = new MemoryContext
        {
            SessionId = "s",
            AssembledAtUtc = DateTimeOffset.UnixEpoch,
            RelevantFacts = new MemoryContextSection<Fact>
            {
                Items = [Fact("f1", "Marta", "lives in", "Lyon", "matrix-marta"), Fact("f2", "Paella", "takes", "two parts stock", null)],
                RankedItems = [new MemoryContextRankedItem("f1", 0.81, 1, 1)],
            },
            RelevantEntities = new MemoryContextSection<Entity>
            {
                Items = [new Entity { EntityId = "e1", Name = "Lyon", Type = "LOCATION", OwnerId = "matrix-pau", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch }],
            },
        };

        var section = Section.Of(context);

        section.Facts.Should().Equal(
            new Hit("Marta | lives in | Lyon", 0.81, 1, "matrix-marta"),
            new Hit("Paella | takes | two parts stock", null, 2, null));
        section.Entities.Should().ContainSingle().Which.Owner.Should().Be("matrix-pau");
    }

    [Fact]
    public void The_world_maps_a_full_name_to_its_alias_and_tells_two_peoples_same_key_apart()
    {
        var world = new WorldIndex(
            [("F01", "fact", "Marta | lives in | Lyon", ""), ("E02", "entity", "Lyon", ""), ("X04", "entity", "Lyon", "")],
            new Dictionary<string, IReadOnlyList<string>> { ["Marta Ruiz"] = ["Marta"] },
            owner: "marta");

        world.IdOf(RecallSection.Facts, new Hit("Marta Ruiz | lives in | Lyon", 0.8, 1, "matrix-marta")).Should().Be("F01");
        world.IdOf(RecallSection.Entities, new Hit("Lyon", 0.7, 1, "matrix-marta")).Should().Be("E02");
        world.IdOf(RecallSection.Entities, new Hit("Lyon", 0.7, 1, "matrix-pau")).Should().Be("X04", "another person's Lyon is a breach");
        world.IdOf(RecallSection.Facts, new Hit("Marta | likes | tea", 0.7, 1, "matrix-marta")).Should().StartWith("?");
    }

    [Fact]
    public void Every_memory_has_the_door_that_holds_it()
    {
        var world = new WorldIndex(
        [
            ("F01", "fact", "Marta | lives in | Lyon", ""), ("D01", "fact", "Marta | count_of:speaks | 3", "derived by the accountant"),
            ("R03", "relationship", "Marta -[SISTER_OF]-> Ane", ""), ("P02", "preference", "Drinks coffee black", ""),
            ("S01", "message", "Hi!", ""), ("T01", "trace", "Book the train", "procedure, booked"), ("T03", "trace", "Plan a weekend", "episode, loved it"),
        ], new Dictionary<string, IReadOnlyList<string>>(), owner: "marta");

        new[] { "F01", "D01", "R03", "P02", "S01", "T01", "T03", "SH2" }.Select(world.DoorOf).Should().Equal(
            Door.Semantic, Door.Derived, Door.EntityGraph, Door.Preference, Door.Episodic, Door.Procedural, Door.Reasoning, Door.Semantic);
    }

    [Fact]
    public void The_local_embedder_reports_its_provider_model_and_dimensions()
    {
        using var embedder = new OllamaEmbeddingGenerator("http://127.0.0.1:11434", "bge-m3") { Dimensions = 1024 };

        var metadata = embedder.GetService(typeof(EmbeddingGeneratorMetadata)).Should().BeOfType<EmbeddingGeneratorMetadata>().Subject;

        metadata.ProviderName.Should().Be("ollama");
        metadata.DefaultModelId.Should().Be("bge-m3");
        metadata.DefaultModelDimensions.Should().Be(1024);
    }

    [Fact]
    public void The_recorder_runs_the_same_Neo4j_image_as_perf() =>
        Recorder.Image.Should().Be(AgentMemory.Cli.Perf.HermeticProfile.Image, "two throwaway stores of one repository, one image");

    private static Fact Fact(string id, string s, string p, string o, string? owner) => new()
    {
        FactId = id, Subject = s, Predicate = p, Object = o, OwnerId = owner, Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
