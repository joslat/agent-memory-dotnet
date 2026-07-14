using FluentAssertions;

namespace AgentMemory.Tests.Unit.Documentation;

/// <summary>
/// CI guard against README.md re-introducing an absolute multi-tenant isolation claim. A null
/// <c>MemoryScope</c> read means "no owner filter" (all owners), and a null write owner stores
/// shared/global memory — isolation is opt-in per call, not automatic. See "Owner isolation" in
/// <c>docs/getting-started.md</c>.
/// </summary>
public sealed class ReadmeConsistencyTests
{
    [Fact]
    public void Readme_DoesNotClaimUnconditionalTenantIsolation()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));

        readme.Should().NotContain("Multi-tenant from day one",
            "isolation is opt-in per call (a null MemoryScope/UserId is global), not automatic");
        readme.Should().NotContain("Can tenants see one another's memory?",
            "the governance table row must reflect that unscoped operations are global by default");
        readme.Should().Contain("Multi-tenant hosts must establish an owner scope",
            "the README must state the host's responsibility to establish an owner scope explicitly");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentMemory.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must run within the repository (AgentMemory.slnx not found above {0})", AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
