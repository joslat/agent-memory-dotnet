using AgentMemory.Abstractions.Repositories;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityReviewContractTests
{
    [Fact]
    public void Atomic_review_has_a_public_default_safe_contract_for_explicit_versioned_operations()
    {
        var method=typeof(IIdentityDecisionRepository).GetMethod("ReviewAsync");
        method.Should().NotBeNull("review/revocation must be an explicit backend operation rather than direct graph editing");
        method!.GetParameters().Should().Contain(parameter=>parameter.ParameterType==typeof(CancellationToken));
        method.IsAbstract.Should().BeFalse("existing backends need a source-compatible unsupported default");
    }

    [Fact]
    public void Revision_history_has_an_explicit_bounded_owner_scoped_read_contract()
    {
        var method=typeof(IIdentityDecisionRepository).GetMethod("GetHistoryAsync");
        method.Should().NotBeNull("a reviewed identity needs immutable inspectable revisions");
        method!.GetParameters().Should().Contain(parameter=>parameter.Name=="ownerId");
        method.GetParameters().Should().Contain(parameter=>parameter.Name=="limit"&&parameter.ParameterType==typeof(int));
        method.IsAbstract.Should().BeFalse();
    }
}
