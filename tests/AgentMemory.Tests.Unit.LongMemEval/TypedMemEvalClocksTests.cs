using AgentEval.Memory.External.TypedMemEval;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 40.66: a TypedMemEval question is recalled at the clocks it asks on. Valid time is the time the question names (the
/// library's own parser, after a leading "As of &lt;date&gt;," that names belief); transaction time is the corpus's as-of
/// instant for a belief question, else the question's date. Read from the corpus as shipped, since AgentEval's entry
/// model drops the block.
/// </summary>
public sealed class TypedMemEvalClocksTests
{
    private static readonly DateTimeOffset Asked = TypedMemEvalClocks.ParseDate("2026/02/05 (Thu) 01:15");

    [Fact]
    public void The_corpus_says_which_clock_each_question_asks_on()
    {
        var asked = TypedMemEvalClocks.Read(TypedMemEvalVertical.Bitemporal);

        asked.Should().HaveCount(60);
        asked.Values.Select(a => a.Clock).Distinct().Should().BeEquivalentTo(["valid", "transaction"]);
        asked.Values.Where(a => a.Clock == "transaction").Should().OnlyContain(a => a.AsOfInstant != null,
            "a belief question names the instant it asks about");
        asked.Values.Where(a => a.Clock == "valid").Should().OnlyContain(a => a.AsOfInstant == null);
    }

    [Fact]
    public void A_question_about_what_is_true_recalls_at_the_time_it_names_and_today_s_belief()
    {
        var (valid, system) = TypedMemEvalClocks.Resolve(
            "Which city does the record show for Alice Renwick in February?", Asked,
            new TypedMemEvalClocks.Asked("Which city does the record show for Alice Renwick in February?", "valid", null));

        (valid.Year, valid.Month).Should().Be((2026, 2), "the question names February");
        system.Should().Be(Asked, "what the record shows now");
    }

    [Fact]
    public void A_question_about_belief_then_recalls_at_the_instant_the_corpus_names()
    {
        const string question = "As of 19 January, which city did the record show for Alice Renwick in February?";
        var instant = TypedMemEvalClocks.ParseDate("2026/01/19 (Mon) 18:15");

        var (valid, system) = TypedMemEvalClocks.Resolve(question, Asked, new TypedMemEvalClocks.Asked(question, "transaction", instant));

        valid.Month.Should().Be(2, "the leading 'As of 19 January,' names belief, so truth is read from 'in February'");
        system.Should().Be(instant);
    }

    [Fact]
    public void A_question_naming_no_time_recalls_at_its_own_date()
    {
        var (valid, system) = TypedMemEvalClocks.Resolve("Which city does Alice live in?", Asked, null);

        valid.Should().Be(Asked);
        system.Should().Be(Asked);
    }

    [Fact]
    public void The_flags_reach_the_options_and_name_the_arm()
    {
        var on = TypedMemEvalProgram.Parse(
            ["--typedmemeval", "bitemporal", "--bitemporal-changes", "--bitemporal-clocks", "--preset", "conversational"]);

        on.BitemporalChanges.Should().BeTrue();
        on.BitemporalClocks.Should().BeTrue();
        on.Arm.FileToken().Should().Be("bitemporal-clocks-presetconversational");
        TypedMemEvalProgram.Parse(["--typedmemeval", "bitemporal"]).Arm.IsDefault.Should().BeTrue(
            "unflagged, a run takes the sealed path every measurement before it took");
    }
}
