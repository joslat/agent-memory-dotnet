using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Infrastructure;

namespace Neo4j.AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// Unit tests for <see cref="PaginationHelper"/> — the N+1 pagination helper.
/// </summary>
public sealed class PaginationHelperTests
{
    // Helper to access internal PaginationHelper via reflection or direct reference.
    // Since PaginationHelper is internal, we use InternalsVisibleTo (already set in project) or test via the assembly.

    [Fact]
    public void ApplyPagination_WhenItemsExceedPageSize_SetsHasNextPageTrue()
    {
        var items = new List<int> { 1, 2, 3, 4, 5, 6 }; // 6 items fetched for pageSize=5

        var result = PaginationHelper.ApplyPagination(items, requestedPageSize: 5);

        result.HasNextPage.Should().BeTrue();
    }

    [Fact]
    public void ApplyPagination_WhenItemsExceedPageSize_TrimsExtraItem()
    {
        var items = new List<int> { 10, 20, 30, 40, 50, 99 }; // 99 is the extra N+1 item

        var result = PaginationHelper.ApplyPagination(items, requestedPageSize: 5);

        result.Items.Should().HaveCount(5);
        result.Items.Should().NotContain(99);
    }

    [Fact]
    public void ApplyPagination_WhenItemsEqualPageSize_HasNextPageFalse()
    {
        // Exactly pageSize items returned → no extra item → no next page
        var items = new List<int> { 1, 2, 3 };

        var result = PaginationHelper.ApplyPagination(items, requestedPageSize: 3);

        result.HasNextPage.Should().BeFalse();
        result.Items.Should().HaveCount(3);
    }

    [Fact]
    public void ApplyPagination_WhenItemsFewerThanPageSize_HasNextPageFalse()
    {
        var items = new List<int> { 1, 2 };

        var result = PaginationHelper.ApplyPagination(items, requestedPageSize: 5);

        result.HasNextPage.Should().BeFalse();
        result.Items.Should().HaveCount(2);
    }

    [Fact]
    public void ApplyPagination_WhenEmptyList_HasNextPageFalse()
    {
        var items = new List<int>();

        var result = PaginationHelper.ApplyPagination(items, requestedPageSize: 10);

        result.HasNextPage.Should().BeFalse();
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public void ApplyPagination_ReturnsCorrectItemsInOrder()
    {
        var items = new List<string> { "a", "b", "c", "d" }; // d is the extra N+1 item

        var result = PaginationHelper.ApplyPagination(items, requestedPageSize: 3);

        result.Items.Should().ContainInOrder("a", "b", "c");
    }

    [Fact]
    public void ApplyPagination_WithPageSizeOne_NextPageDetected()
    {
        var items = new List<int> { 42, 99 }; // N+1 = 2 fetched for pageSize=1

        var result = PaginationHelper.ApplyPagination(items, requestedPageSize: 1);

        result.HasNextPage.Should().BeTrue();
        result.Items.Should().ContainSingle().Which.Should().Be(42);
    }
}
