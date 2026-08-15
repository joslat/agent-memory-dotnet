using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Extraction.Derivation;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.6 step 4. The arithmetic itself — pure, deterministic, and the only part that can be wrong
/// silently.
/// </summary>
/// <remarks>
/// <para>
/// The pre-registered gate for this feature is <b>100% exact</b>: a single wrong derived value rejects
/// it outright, because a stored wrong number is a manufactured confident-wrong answer and worse than
/// no answer at all. These tests are where that standard is actually enforced, since everything
/// downstream — persistence, cascade, rendering — faithfully carries whatever the evaluators computed.
/// </para>
/// <para>
/// The recurring theme below is <b>refusing</b>. An evaluator that guesses when its inputs are
/// ambiguous produces a number that looks exactly like a correct one and arrives carrying inline
/// provenance that makes it look checked.
/// </para>
/// </remarks>
public sealed class DerivationEvaluatorTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Fact F(
        string id, string @object, int order = 0, DateTimeOffset? validFrom = null) => new()
    {
        FactId = id,
        Subject = "user",
        Predicate = "savings_balance",
        Object = @object,
        Confidence = 0.9,
        CreatedAtUtc = Base.AddDays(order),
        ValidFrom = validFrom,
    };

    private static DerivationGroup Group(
        IEnumerable<Fact> facts,
        string predicate = "savings_balance",
        string predicateKey = "savings_balance",
        DerivedMemoryOptions? options = null) =>
        new("user", predicate, predicateKey, [.. facts], options ?? new DerivedMemoryOptions());

    // ── Count ─────────────────────────────────────────────────────────

    [Fact]
    public void CountReportsTheGroupSize()
    {
        var result = new CountEvaluator().Evaluate(
            Group([F("a", "one", 0), F("b", "two", 1), F("c", "three", 2)]));

        result.Should().NotBeNull();
        result!.Object.Should().Be("3");
        result.Predicate.Should().Be("count_of:savings_balance");
        result.InputFactIds.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void CountWorksOnNonNumericObjectsBecauseCountingNeverNeededTheNumber()
    {
        new CountEvaluator().Evaluate(Group([F("a", "lots"), F("b", "several")]))
            .Should().NotBeNull();
    }

    // ── Delta ─────────────────────────────────────────────────────────

    [Fact]
    public void DeltaSubtractsTheFirstValueFromTheLast()
    {
        // The adjudicated case: the store holds 800 and 50; the answer is -750.
        var result = new DeltaEvaluator().Evaluate(
            Group([F("a", "800", 0), F("b", "50", 1)]));

        result.Should().NotBeNull();
        result!.Object.Should().Be("-750");
        result.Derivation.Should().Be("50 (b) - 800 (a)");
    }

    [Fact]
    public void DeltaReadsTheChainInOrderNotAsASet()
    {
        // Direction is the answer. A delta over an unordered group subtracts two arbitrary members.
        var ascending = new DeltaEvaluator().Evaluate(Group([F("a", "50", 0), F("b", "800", 1)]));

        ascending!.Object.Should().Be("750");
    }

    [Fact]
    public void DeltaRefusesAGroupContainingAnyUnparsableValue()
    {
        // Computing over the parsable subset would answer a different question: the change between two
        // values that happened to be readable is not the change over the chain.
        new DeltaEvaluator().Evaluate(Group([F("a", "800", 0), F("b", "lots", 1), F("c", "50", 2)]))
            .Should().BeNull();
    }

    [Fact]
    public void DeltaAcceptsCurrencyAndThousandsSeparators()
    {
        var result = new DeltaEvaluator().Evaluate(Group([F("a", "$1,800", 0), F("b", "$1,050", 1)]));

        result!.Object.Should().Be("-750");
    }

    // ── Latest ────────────────────────────────────────────────────────

    [Fact]
    public void LatestReportsTheMostRecentValueAndNamesThePreviousOne()
    {
        var result = new LatestEvaluator().Evaluate(
            Group([F("a", "Acme", 0), F("b", "Initech", 1)]));

        result.Should().NotBeNull();
        result!.Object.Should().Be("Initech");
        result.Derivation.Should().Contain("previously 'Acme'");
    }

    [Fact]
    public void LatestPrefersValidTimeOverLearnedTime()
    {
        // "Learned yesterday about 2019" must sort as 2019, or the latest value is whichever was
        // extracted last rather than whichever is most recently true.
        var learnedLast = F("late-learn", "old news", order: 5, validFrom: Base.AddYears(-3));
        var learnedFirst = F("early-learn", "current", order: 0, validFrom: Base);
        var ordered = new[] { learnedLast, learnedFirst }
            .OrderBy(DerivationGroup.EffectiveAt)
            .ToList();

        var result = new LatestEvaluator().Evaluate(Group(ordered));

        result!.Object.Should().Be("current");
    }

    // ── Sum ───────────────────────────────────────────────────────────

    [Fact]
    public void SumRefusesAPredicateThatIsNotOnTheAllowlist()
    {
        // The allowlist IS the safety story: adding three temperatures is arithmetically perfect and
        // semantically meaningless, and no audit of the arithmetic would catch it.
        new SumEvaluator().Evaluate(Group([F("a", "12", 0), F("b", "5", 1)]))
            .Should().BeNull();
    }

    [Fact]
    public void SumAddsAnAllowlistedPredicate()
    {
        var options = new DerivedMemoryOptions();
        options.AdditivePredicateKeys.Add("fish_count");

        var result = new SumEvaluator().Evaluate(Group(
            [F("a", "12", 0), F("b", "5", 1)],
            predicate: "fish_count", predicateKey: "fish_count", options: options));

        result.Should().NotBeNull();
        result!.Object.Should().Be("17");
        result.Derivation.Should().Be("12 (a) + 5 (b)");
    }

    [Fact]
    public void SumRefusesAGroupContainingAnUnparsableValue()
    {
        var options = new DerivedMemoryOptions();
        options.AdditivePredicateKeys.Add("fish_count");

        new SumEvaluator().Evaluate(Group(
                [F("a", "12", 0), F("b", "lots", 1)],
                predicate: "fish_count", predicateKey: "fish_count", options: options))
            .Should().BeNull();
    }

    [Fact]
    public void TheAllowlistMatchIsCaseInsensitive()
    {
        var options = new DerivedMemoryOptions();
        options.AdditivePredicateKeys.Add("Fish_Count");

        new SumEvaluator().Evaluate(Group(
                [F("a", "12", 0), F("b", "5", 1)],
                predicate: "fish_count", predicateKey: "fish_count", options: options))
            .Should().NotBeNull();
    }

    // ── Duration ──────────────────────────────────────────────────────

    [Fact]
    public void DurationMeasuresBetweenRealValidTimes()
    {
        var result = new DurationEvaluator().Evaluate(Group(
        [
            F("a", "started", validFrom: new DateTimeOffset(2023, 5, 1, 0, 0, 0, TimeSpan.Zero)),
            F("b", "ended", validFrom: new DateTimeOffset(2023, 5, 31, 0, 0, 0, TimeSpan.Zero)),
        ]));

        result.Should().NotBeNull();
        result!.Object.Should().Be("P30D");
        result.Derivation.Should().Contain("30 days");
    }

    [Fact]
    public void DurationRefusesFactsThatHaveOnlyAnExtractionTimestamp()
    {
        // An interval between two created_at values measures when the system was TOLD things, not when
        // they happened -- fiction with a plausible shape, which is the worst kind of wrong answer.
        new DurationEvaluator().Evaluate(Group([F("a", "started", 0), F("b", "ended", 30)]))
            .Should().BeNull();
    }

    [Fact]
    public void DurationRefusesAZeroLengthInterval()
    {
        var same = new DateTimeOffset(2023, 5, 1, 0, 0, 0, TimeSpan.Zero);

        new DurationEvaluator().Evaluate(Group(
                [F("a", "x", validFrom: same), F("b", "y", validFrom: same)]))
            .Should().BeNull();
    }

    // ── Set enumeration ───────────────────────────────────────────────

    [Fact]
    public void EnumerationSortsAndListsDistinctValues()
    {
        var result = new SetEnumerationEvaluator().Evaluate(Group(
            [F("a", "Rome", 0), F("b", "Lisbon", 1), F("c", "Paris", 2)],
            predicate: "visited_city", predicateKey: "visited_city"));

        result.Should().NotBeNull();
        result!.Object.Should().Be("Lisbon; Paris; Rome");
        result.Predicate.Should().Be("set_of:visited_city");
    }

    [Fact]
    public void EnumerationDeduplicatesCaseInsensitivelyLikeTheGraphDoes()
    {
        // Listing "Paris" and "paris" as two cities is a wrong answer produced by correct code.
        var result = new SetEnumerationEvaluator().Evaluate(Group(
            [F("a", "Paris", 0), F("b", "paris", 1), F("c", "Rome", 2)],
            predicate: "visited_city", predicateKey: "visited_city"));

        result!.Object.Should().Be("Paris; Rome");
    }

    [Fact]
    public void EnumerationSaysSoWhenItCapsTheList()
    {
        // A capped list read as complete is a wrong answer, and the model has no other way to know.
        var options = new DerivedMemoryOptions { MaxEnumerationItems = 2 };

        var result = new SetEnumerationEvaluator().Evaluate(Group(
            [F("a", "Lisbon", 0), F("b", "Paris", 1), F("c", "Rome", 2)],
            predicate: "visited_city", predicateKey: "visited_city", options: options));

        result!.Object.Should().Be("Lisbon; Paris");
        result.Derivation.Should().Contain("1 more not listed");
    }

    [Fact]
    public void TwoFactsSayingTheSameThingAreARestatementNotASet()
    {
        new SetEnumerationEvaluator().Evaluate(Group(
                [F("a", "Paris", 0), F("b", "Paris", 1)],
                predicate: "visited_city", predicateKey: "visited_city"))
            .Should().BeNull();
    }

    // ── the shared floor ──────────────────────────────────────────────

    // Keyed by NAME rather than by instance: IDerivationEvaluator is internal, and a public test method
    // cannot take an internal parameter type. Resolving inside the test keeps every signature public so
    // xUnit still discovers the class.
    public static TheoryData<string> AllEvaluators() =>
    [
        nameof(CountEvaluator), nameof(DeltaEvaluator), nameof(LatestEvaluator),
        nameof(SumEvaluator), nameof(DurationEvaluator), nameof(SetEnumerationEvaluator),
    ];

    private static IDerivationEvaluator Resolve(string name) => name switch
    {
        nameof(CountEvaluator) => new CountEvaluator(),
        nameof(DeltaEvaluator) => new DeltaEvaluator(),
        nameof(LatestEvaluator) => new LatestEvaluator(),
        nameof(SumEvaluator) => new SumEvaluator(),
        nameof(DurationEvaluator) => new DurationEvaluator(),
        nameof(SetEnumerationEvaluator) => new SetEnumerationEvaluator(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown evaluator."),
    };

    [Theory]
    [MemberData(nameof(AllEvaluators))]
    public void NoEvaluatorAggregatesASingleFact(string evaluatorName)
    {
        // An "aggregate" of one is the fact restated, occupying a second slot in the same budget its
        // input already occupies -- and carrying derived provenance for arithmetic never performed.
        Resolve(evaluatorName).Evaluate(Group([F("a", "800", 0)])).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(AllEvaluators))]
    public void NoEvaluatorAggregatesAnEmptyGroup(string evaluatorName)
    {
        Resolve(evaluatorName).Evaluate(Group([])).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(AllEvaluators))]
    public void EveryCandidateNamesTheFactsItWasComputedFrom(string evaluatorName)
    {
        // The audit depends on this: a derived value is recomputed out-of-band from its recorded inputs
        // and compared exactly. A candidate with no inputs cannot be audited, so it cannot be trusted.
        var options = new DerivedMemoryOptions { MaxEnumerationItems = 10 };
        options.AdditivePredicateKeys.Add("savings_balance");
        var group = Group(
        [
            F("a", "12", 0, validFrom: new DateTimeOffset(2023, 5, 1, 0, 0, 0, TimeSpan.Zero)),
            F("b", "5", 1, validFrom: new DateTimeOffset(2023, 5, 31, 0, 0, 0, TimeSpan.Zero)),
        ], options: options);
        var evaluator = Resolve(evaluatorName);

        var candidate = evaluator.Evaluate(group);

        candidate.Should().NotBeNull("this fixture is built to satisfy every operator");
        candidate!.InputFactIds.Should().NotBeEmpty();
        candidate.Derivation.Should().NotBeNullOrWhiteSpace();
        candidate.Operator.Should().Be(evaluator.Operator);
    }
}
