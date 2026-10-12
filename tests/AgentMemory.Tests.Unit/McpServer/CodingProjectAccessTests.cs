using AgentMemory.McpHost.Coding;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingProjectAccessTests
{
    private static CodingServiceOptions Options => new()
    {
        OwnerId = "owner", Projects = new Dictionary<string, string> { ["alpha"] = "ALPHA_KEY", ["beta"] = "BETA_KEY" },
    };

    [Fact]
    public void Credentials_select_only_their_configured_owner_and_project()
    {
        var access = new CodingProjectAccess(Options, name => name == "ALPHA_KEY" ? new string('a', 40) : new string('b', 40));
        access.Authorize("alpha", "Bearer " + new string('a', 40)).Should().Be(new CodingIdentity("owner", "alpha"));
        access.Authorize("beta", "Bearer " + new string('a', 40)).Should().BeNull();
        access.Authorize("unknown", "Bearer " + new string('a', 40)).Should().BeNull();
        access.Authorize("alpha", "").Should().BeNull();
    }

    [Theory]
    [InlineData("Alpha")]
    [InlineData("a--b")]
    [InlineData("../alpha")]
    [InlineData("a_b")]
    [InlineData("alpha\n")]
    public void Project_aliases_that_could_collapse_to_the_same_database_are_rejected(string project)
    {
        var options = Options with { Projects = new Dictionary<string, string> { [project] = "KEY" } };
        options.Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("                                ")]
    public void Missing_or_weak_credentials_fail_startup_without_revealing_the_value(string? token)
    {
        var create = () => new CodingProjectAccess(Options, _ => token);
        create.Should().Throw<ArgumentException>().WithMessage("Each coding project requires a distinct bearer token of 32 to 256 printable ASCII characters without spaces.");
    }

    [Fact]
    public void Reusing_one_credential_for_two_projects_fails_startup()
    {
        var create = () => new CodingProjectAccess(Options, _ => new string('x', 40));
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Configuration_and_environment_changes_cannot_rebind_an_existing_credential()
    {
        var projects = new Dictionary<string, string> { ["alpha"] = "KEY" };
        var token = new string('a', 40);
        var access = new CodingProjectAccess(Options with { Projects = projects }, _ => token);
        projects.Clear();
        projects["beta"] = "KEY";
        token = new string('b', 40);
        access.Authorize("alpha", "bearer " + new string('a', 40)).Should().Be(new CodingIdentity("owner", "alpha"));
        access.Authorize("alpha", "Bearer " + token).Should().BeNull();
        access.Authorize("beta", "Bearer " + new string('a', 40)).Should().BeNull();
    }

    [Theory]
    [InlineData("http://0.0.0.0:5234")]
    [InlineData("http://localhost:5234")]
    [InlineData("http://127.0.0.1:5234/path")]
    [InlineData("http://127.0.0.1:5234/?key=secret")]
    [InlineData("http://user@127.0.0.1:5234")]
    [InlineData("https://127.0.0.1:5234")]
    public void Configuration_rejects_other_listeners(string url)
    {
        var options = Options with { Url = url };
        options.Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
    }
}
