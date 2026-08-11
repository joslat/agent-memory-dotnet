using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// Entity names are range-indexed and unbounded, so an oversized one fails at write time.
/// </summary>
/// <remarks>
/// <c>entity_name_idx</c> and <c>entity_canonical_idx</c> are range indexes over LLM-produced text
/// (<c>SchemaQueries.cs:98,101</c>), and the only length rule anywhere in the codebase is a
/// <b>minimum</b> — <c>ExtractionOptions.MinNameLength = 2</c>. Neo4j caps an index key at roughly
/// 8 KB, so a pathological name makes the driver throw
/// <c>Property value is too large to index</c> from inside the write.
/// <para>
/// This guard changes <b>no successful write</b>. Everything it rejects already fails today; it only
/// fails earlier, names the offending property, and reports a typed error instead of surfacing a
/// driver message from the middle of a batch. That is what makes it safe to add to a shipped write
/// path — it narrows nothing.
/// </para>
/// </remarks>
public sealed class IndexKeyBudgetTests
{
    [Theory]
    [InlineData("Alice")]
    [InlineData("")]
    [InlineData(null)]
    public void OrdinaryValuesPassUntouched(string? value)
    {
        // The property that matters most: normal data must be completely unaffected.
        var act = () => IndexKeyBudget.EnsureIndexable(value, "name", "entity-1");
        act.Should().NotThrow();
    }

    [Fact]
    public void AValueAtTheLimitIsStillAccepted()
    {
        // Boundary: the guard must not be stricter than the store it protects.
        var atLimit = new string('a', IndexKeyBudget.MaxIndexedBytes);
        var act = () => IndexKeyBudget.EnsureIndexable(atLimit, "name", "entity-1");
        act.Should().NotThrow();
    }

    [Fact]
    public void AnOversizedValueIsRejectedWithTheOffendingProperty()
    {
        var oversized = new string('a', IndexKeyBudget.MaxIndexedBytes + 1);

        var act = () => IndexKeyBudget.EnsureIndexable(oversized, "canonical_name", "entity-7");

        act.Should().Throw<MemoryException>()
            .Which.Message.Should().ContainAll("canonical_name", "entity-7");
    }

    [Fact]
    public void LengthIsMeasuredInUtf8BytesNotCharacters()
    {
        // The load-bearing subtlety. Neo4j's limit is on the encoded key, and a multi-byte name is
        // three times its character count in UTF-8 — so a char-based check would let a value through
        // that the index then rejects, which is exactly the failure being prevented.
        var multiByte = new string('☕', IndexKeyBudget.MaxIndexedBytes / 2);

        var act = () => IndexKeyBudget.EnsureIndexable(multiByte, "name", "entity-2");

        act.Should().Throw<MemoryException>(
            "each ☕ is 3 UTF-8 bytes, so this exceeds the byte budget while being under it in chars");
    }
}
