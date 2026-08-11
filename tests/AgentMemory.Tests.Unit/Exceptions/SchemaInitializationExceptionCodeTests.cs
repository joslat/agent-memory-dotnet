using AgentMemory.Abstractions.Exceptions;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Exceptions;

/// <summary>
/// L10. <see cref="SchemaInitializationException"/> must be able to carry a structured
/// <see cref="MemoryException.Code"/>, not only a free-text operation name.
/// </summary>
/// <remarks>
/// <see cref="MemoryException.Code"/> is settable only through the assembly-internal builder
/// constructor, and <c>MemoryError.Create(...).Build()</c> returns the <b>base</b> type — so before
/// this constructor existed there was no way to throw a schema exception that was both typed and
/// coded. The nearest-looking call, <c>new SchemaInitializationException(msg, code)</c>, compiles and
/// silently files the code under <see cref="SchemaInitializationException.SchemaOperation"/>, leaving
/// <c>Code</c> null: a handler switching on the code sees nothing, and the mistake is invisible at
/// the call site. The first assertion here is what pins that down.
/// </remarks>
public sealed class SchemaInitializationExceptionCodeTests
{
    [Fact]
    public void CodeAndOperationAreCarriedSeparately()
    {
        var ex = new SchemaInitializationException(
            "boom", "validate-index-state", MemoryErrorCodes.SchemaBootstrapFailed);

        ex.Code.Should().Be(MemoryErrorCodes.SchemaBootstrapFailed);
        ex.SchemaOperation.Should().Be("validate-index-state");
        ex.Message.Should().Be("boom");
    }

    [Fact]
    public void TwoArgumentOverloadStillLeavesCodeNull()
    {
        // Documents the trap rather than endorsing it: this overload sets the operation only.
        new SchemaInitializationException("boom", "validate-index-state").Code.Should().BeNull();
    }

    [Fact]
    public void IsCatchableAsMemoryException()
    {
        new SchemaInitializationException("boom", "op", MemoryErrorCodes.SchemaBootstrapFailed)
            .Should().BeAssignableTo<MemoryException>();
    }
}
