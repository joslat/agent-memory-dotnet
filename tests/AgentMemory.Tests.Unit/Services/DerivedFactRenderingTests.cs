using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Mapping;
using AgentMemory.Core.Services;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.6 step 10. The arithmetic renders inline — on both surfaces, and only for derived facts.
/// </summary>
/// <remarks>
/// <para>
/// The point of rendering the derivation is that a stored number is only as good as the model's ability
/// to <b>check</b> it. <c>17</c> is a claim; <c>17 — derived: 12 (a1) + 5 (b2)</c> is an argument, and a
/// model handed the second can notice when the arithmetic does not follow from what it was told.
/// </para>
/// <para>
/// The both-surfaces assertion is not redundancy. Core's formatter and the Agent Framework mapper have
/// drifted apart before over exactly this kind of decision, most recently a procedure-trust clause fixed
/// in the harness while the product shipped the contradiction.
/// </para>
/// </remarks>
public sealed class DerivedFactRenderingTests
{
    private static readonly DateTimeOffset Stamp = DateTimeOffset.UnixEpoch;

    private static Fact Plain() => new()
    {
        FactId = "f1", Subject = "user", Predicate = "total_fish_count", Object = "17",
        Confidence = 0.9, CreatedAtUtc = Stamp,
    };

    private static Fact WithDerivation() => Plain() with
    {
        Metadata = MemoryDerivationMetadataExtensions.CreateWithDerivation(
            DerivationOperators.Sum, "12 (a1) + 5 (b2)", ["a1", "b2"]),
    };

    private static RecallResult Result(Fact fact) => new()
    {
        Context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = Stamp,
            RelevantFacts = new MemoryContextSection<Fact> { Items = [fact] },
        },
        TotalItemsRetrieved = 1,
    };

    // ── Core formatter ────────────────────────────────────────────────

    [Fact]
    public void TheCoreFormatterRendersTheArithmeticInline()
    {
        var formatted = MemoryContextFormatter.FormatRecallResult(Result(WithDerivation()));

        formatted.Should().Contain("user total_fish_count 17 — derived: 12 (a1) + 5 (b2)");
    }

    [Fact]
    public void AnOrdinaryFactRendersByteIdenticallyToBefore()
    {
        // The off-state guarantee at the rendering layer. A fact with no derivation metadata must
        // produce the exact string it always did -- every sealed prompt fingerprint depends on it.
        var formatted = MemoryContextFormatter.FormatRecallResult(Result(Plain()));

        // The line ends after the object, with nothing appended. Asserted on the rendered line rather
        // than on a whole-prompt hash, because the sealed fingerprint tests already hold that at the
        // prompt level and this one has to say WHICH line it is about.
        formatted.Should().Contain("- user total_fish_count 17");
        formatted.Should().NotContain("derived:");
        formatted.Should().NotContain("—");
    }

    // ── Agent Framework mapper ────────────────────────────────────────

    [Fact]
    public void TheAgentFrameworkMapperRendersTheSameArithmetic()
    {
        var messages = MafTypeMapper.ToContextMessages(
            Result(WithDerivation()).Context, new ContextFormatOptions());

        string.Join("\n", messages.Select(m => m.Text))
            .Should().Contain("user total_fish_count 17 — derived: 12 (a1) + 5 (b2)");
    }

    [Fact]
    public void TheAgentFrameworkMapperLeavesAnOrdinaryFactAlone()
    {
        var messages = MafTypeMapper.ToContextMessages(
            Result(Plain()).Context, new ContextFormatOptions());

        string.Join("\n", messages.Select(m => m.Text)).Should().NotContain("derived:");
    }

    // ── the shared renderer ───────────────────────────────────────────

    [Fact]
    public void AnEmptyDerivationStringIsTreatedAsAbsent()
    {
        // A trailing "— derived:" with nothing after it would be worse than no provenance: it asserts
        // that arithmetic was shown when none was.
        var fact = Plain() with
        {
            Metadata = new Dictionary<string, object> { ["kind"] = "derived", ["derivation"] = "   " },
        };

        DerivedFactRenderer.Append("line", fact).Should().Be("line");
    }

    [Fact]
    public void TheDerivationSurvivesTheDelimitersEscaping()
    {
        // The lesson delta recall learned the hard way: every admitted block is HTML-escaped, so a "->"
        // in a derivation string would reach the model as "-&gt;". The evaluators render "+" and "-",
        // never an arrow, and this is what keeps that true.
        var formatted = MemoryContextFormatter.FormatRecallResult(Result(WithDerivation()));

        formatted.Should().NotContain("&gt;");
        formatted.Should().NotContain("&lt;");
    }
}
