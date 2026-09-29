using System.Text.Json;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// <c>--question-ids</c> narrows the usual draw. It never samples, and it never reaches outside the draw.
/// </summary>
/// <remarks>
/// The pilot this exists for is ten questions out of the August reference 50. Its value is that it is a
/// subset of that 50, so its numbers can be read against the 50's. A filter that quietly ran an id from
/// some other draw would break exactly that, so an id outside the draw stops the run and is named.
/// </remarks>
public sealed class QuestionFilterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("lme-qids-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void TheKeptQuestionsRunInDrawOrderWhateverOrderTheyWereTypedIn()
    {
        var filter = LongMemEvalQuestionFilter.Parse("d,b")!;

        filter.SelectFrom(["a", "b", "c", "d"], "test draw").Should().Equal("b", "d");
    }

    [Fact]
    public void AnIdOutsideTheDrawFailsTheRunAndIsNamed()
    {
        var filter = LongMemEvalQuestionFilter.Parse("b,x_abs,y")!;

        var act = () => filter.SelectFrom(["a", "b", "c"], "3 drawn with seed 42");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("x_abs, y").And.Contain("3 drawn with seed 42");
    }

    [Fact]
    public void AnIdFileTakesLinesCommasAndComments()
    {
        var file = Path.Combine(_directory, "ids.txt");
        File.WriteAllText(file, "# the chosen ten\n51c32626\na96c20ee_abs, 0bc8ad92\n\n");

        var filter = LongMemEvalQuestionFilter.Parse("@" + file)!;

        filter.RequestedIds.Should().Equal("51c32626", "a96c20ee_abs", "0bc8ad92");
        filter.Specification.Should().Be("@" + Path.GetFullPath(file));
    }

    [Theory]
    [InlineData("a,b,a", "*a more than once*")]
    [InlineData(" , ", "*names no question*")]
    public void ADuplicateOrAnEmptyListIsRefusedRatherThanTidied(string value, string message)
    {
        var act = () => LongMemEvalQuestionFilter.Parse(value);

        act.Should().Throw<ArgumentException>().WithMessage(message);
    }

    [Fact]
    public void NoFlagMeansNoFilter() => LongMemEvalQuestionFilter.Parse(null).Should().BeNull();

    /// <summary>
    /// The derived dataset carries each kept entry exactly as the source wrote it, in the order given.
    /// </summary>
    [Fact]
    public void TheDerivedDatasetCopiesEachEntryVerbatimInTheGivenOrder()
    {
        var source = Path.Combine(_directory, "source.json");
        File.WriteAllText(source, """
            [
              { "question_id": "a", "question": "café?",   "n": 1.50 },
              { "question_id": "b", "question": "b?", "n": 2 },
              { "question_id": "c", "question": "c?", "nested": { "k": [1, 2] } }
            ]
            """);

        var (path, sha) = LongMemEvalFilteredDataset.Write(source, ["c", "a"], _directory);

        var written = File.ReadAllText(path);
        written.Should().Be(
            "[" + """{ "question_id": "c", "question": "c?", "nested": { "k": [1, 2] } }""" + ","
                + """{ "question_id": "a", "question": "café?",   "n": 1.50 }""" + "]");
        Path.GetFileName(path).Should().Be($"longmemeval-filtered-{sha[..16]}.json");
        LongMemEvalFilteredDataset.Write(source, ["c", "a"], _directory).Sha256.Should().Be(sha);
    }

    [Fact]
    public void AnIdTheDatasetDoesNotHoldIsRefused()
    {
        var source = Path.Combine(_directory, "source.json");
        File.WriteAllText(source, """[ { "question_id": "a" } ]""");

        var act = () => LongMemEvalFilteredDataset.Write(source, ["a", "zz"], _directory);

        act.Should().Throw<InvalidOperationException>().WithMessage("*zz*");
    }

    /// <summary>End to end through AgentEval: draw, narrow, derive, and re-draw the same questions.</summary>
    [Fact]
    public void TheFilteredRunReDrawsExactlyTheKeptQuestionsWithTheirOriginalHistories()
    {
        var source = Path.Combine(_directory, "dataset.json");
        File.WriteAllText(source, JsonSerializer.Serialize(new[]
        {
            Entry("q-user", "single-session-user"),
            Entry("q-multi", "multi-session"),
            Entry("q-temporal", "temporal-reasoning"),
            Entry("q-update", "knowledge-update"),
        }));
        var drawnOptions = LongMemEvalBenchmarkProtocol.CreateOptions(
            source, 4, 42, 2, LongMemEvalEvidenceDetail.Identifiers, 30);
        var drawn = LongMemEvalEvidenceIndex.Load(source, drawnOptions)
            .Questions.Select(question => question.QuestionId).ToArray();
        var keep = new[] { drawn[3], drawn[1] };

        var selection = LongMemEvalQuestionSelection.Resolve(
            source,
            drawnOptions,
            LongMemEvalQuestionFilter.Parse(string.Join(",", keep)),
            (path, count) => LongMemEvalBenchmarkProtocol.CreateOptions(
                path, count, 42, 2, LongMemEvalEvidenceDetail.Identifiers, 30),
            _directory);

        selection.QuestionCount.Should().Be(2);
        selection.EvidenceIndex!.Questions.Select(question => question.QuestionId)
            .Should().Equal(drawn[1], drawn[3]);
        selection.Options.DatasetPath.Should().Be(selection.DatasetPath);
        selection.Record!.DrawnQuestions.Should().Be(4);
        selection.Record.DerivedDatasetSha256.Should().HaveLength(64);
    }

    [Fact]
    public void WithoutAFilterTheDrawPassesThroughUntouched()
    {
        var options = LongMemEvalBenchmarkProtocol.CreateOptions(
            "unused.json", 50, 42, 2, LongMemEvalEvidenceDetail.Identifiers, 30);

        var selection = LongMemEvalQuestionSelection.Resolve(
            "unused.json", options, null, (_, _) => throw new InvalidOperationException("not called"));

        selection.Options.Should().BeSameAs(options);
        selection.DatasetPath.Should().Be("unused.json");
        selection.EvidenceIndex.Should().BeNull();
        selection.Record.Should().BeNull();
    }

    [Fact]
    public void BothVerbsAcceptTheFlag()
    {
        LongMemEvalPreparedPairProgram.KnownOptions.Should().Contain("--question-ids");
        LongMemEvalPreparedPairProgram.Parse(["--prepared-pair", "--question-ids", "a,b"])
            .QuestionFilter!.RequestedIds.Should().Equal("a", "b");

        var dataset = Path.Combine(_directory, "d.json");
        File.WriteAllText(dataset, "[]");
        LongMemEvalReferenceArmProgram.Parse(
                ["--reference-arm", "no-memory", "--dataset", dataset, "--question-ids", "a,b"])
            .QuestionFilter!.RequestedIds.Should().Equal("a", "b");
    }

    private static object Entry(string id, string type) => new Dictionary<string, object>
    {
        ["question_id"] = id,
        ["question_type"] = type,
        ["question"] = $"What about {id}?",
        ["answer"] = $"answer for {id}",
        ["question_date"] = "2023/05/30 (Tue) 23:40",
        ["haystack_session_ids"] = new[] { $"{id}-s1", $"{id}-s2" },
        ["haystack_dates"] = new[] { "2023/05/20 (Sat) 02:21", "2023/05/21 (Sun) 10:00" },
        ["haystack_sessions"] = new[]
        {
            new object[]
            {
                new { role = "user", content = $"first session for {id}", has_answer = true },
                new { role = "assistant", content = "noted" },
            },
            new object[]
            {
                new { role = "user", content = $"second session for {id}", has_answer = false },
                new { role = "assistant", content = "ok" },
            },
        },
        ["answer_session_ids"] = new[] { $"{id}-s1" },
    };
}
