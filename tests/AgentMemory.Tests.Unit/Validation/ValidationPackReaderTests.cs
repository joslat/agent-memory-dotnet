using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Validation;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Validation;

/// <summary>40.57: a pack is checked before it runs, so a mistake in the pack never reads as a defect in memory.</summary>
public sealed class ValidationPackReaderTests
{
    [Fact]
    public void Every_core_pack_is_well_formed()
    {
        var packs = ValidationPackReader.Core();

        packs.Should().NotBeEmpty();
        foreach (var pack in packs)
            ValidationPackReader.Check(pack).Should().BeEmpty($"core pack '{pack.Id}' is well formed");
        packs.Select(p => p.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_pack_with_one_owner_cannot_check_isolation()
    {
        var pack = Minimal() with { Owners = ["ana"] };

        ValidationPackReader.Check(pack).Should().ContainSingle(p => p.Contains("two distinct owners"));
    }

    [Fact]
    public void A_predicate_outside_the_schema_is_named()
    {
        var pack = Minimal() with
        {
            Sessions = [new PackSession
            {
                Owner = "ana", Id = "s1",
                Messages = [new PackMessage { At = DateTimeOffset.UnixEpoch, Text = "x", Facts = [new PackFact { Subject = "Ana", Predicate = "owns", Object = "a boat" }] }],
            }],
        };

        ValidationPackReader.Check(pack).Should().ContainSingle(p => p.Contains("predicate 'owns' is not in the schema"));
    }

    [Fact]
    public void Items_name_exactly_one_thing_and_triples_have_three_parts()
    {
        var pack = Minimal() with
        {
            Questions = [new PackQuestion
            {
                Id = "q", Owner = "ana", At = DateTimeOffset.UnixEpoch, Ask = "?",
                Expect = [new PackItem { Fact = "Ana | lives in" }, new PackItem { Fact = "Ana | lives in | Bilbao", Entity = "Ana" }],
            }],
        };

        var problems = ValidationPackReader.Check(pack);

        problems.Should().Contain(p => p.Contains("'Ana | lives in' is not 'subject | predicate | object'"));
        problems.Should().Contain(p => p.Contains("an item sets 2 fields"));
    }

    [Fact]
    public void Unknown_fields_are_refused_rather_than_ignored()
    {
        var act = () => ValidationPackReader.Parse("""{ "format": "agentmemory-pack/1", "id": "x", "title": "x", "owner": ["a"] }""");

        act.Should().Throw<JsonException>("a misspelt field would otherwise silently check nothing");
    }

    [Theory]
    [InlineData("Extraction.BitemporalChanges", true)]
    [InlineData("ResolveTemporalQueries", true)]
    public void A_switch_is_set_by_its_path(string path, bool value)
    {
        var options = new MemoryOptions();

        OptionPaths.Apply(options, path, JsonSerializer.SerializeToElement(value)).Should().BeNull();

        (path == "ResolveTemporalQueries" ? options.ResolveTemporalQueries : options.Extraction.BitemporalChanges).Should().Be(value);
    }

    [Theory]
    [InlineData("Recall.MaxFacts", "not a settable option")]
    [InlineData("Extraction.NoSuchSwitch", "not a settable option")]
    [InlineData("Nowhere.BitemporalChanges", "no settable options")]
    public void A_switch_that_cannot_be_set_says_why(string path, string reason)
    {
        var problem = OptionPaths.Apply(new MemoryOptions(), path, JsonSerializer.SerializeToElement(3));

        problem.Should().Contain(reason);
        RecallOptions.Default.MaxFacts.Should().Be(10, "the shared default is never touched");
    }

    private static ValidationPack Minimal() => new()
    {
        Format = ValidationPackReader.Format,
        Id = "test.minimal",
        Title = "minimal",
        Owners = ["ana", "bob"],
        Schema = new PackSchema { Predicates = [new PackPredicate { Name = "lives in", Single = true }] },
        Questions = [new PackQuestion { Id = "q", Owner = "ana", At = DateTimeOffset.UnixEpoch, Ask = "?", Expect = [new PackItem { Entity = "Ana" }] }],
    };
}
