using FluentAssertions;

namespace AgentMemory.Tests.Unit.Samples;

/// <summary>
/// Every sample that resolves a model says which one answered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because three of eleven silently did not.</b> The banner was inserted by a pass that
/// keyed on what followed the guard block, which is only the same in some samples; the other three
/// built their clients, ran correctly, and never told the operator which host they were talking to.
/// Nothing failed, so nothing surfaced it — a live run did.
/// </para>
/// <para>
/// It matters more than a missing log line sounds. With a provider layer there are five hosts a
/// sample could be on, and this machine already auto-detects a stale Azure deployment when the
/// selector is unset. "It ran" and "it ran against what I meant" stop being the same statement, and
/// the banner is the only thing that distinguishes them.
/// </para>
/// </remarks>
public sealed class EverySampleReportsItsProviderTests
{
    [Fact]
    public void EverySampleThatResolvesAModelPrintsTheBanner()
    {
        var samples = Directory
            .EnumerateFiles(FindRepoDirectory("samples"), "Program.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Source: File.ReadAllText(path)))
            .Where(s => s.Source.Contains("RealModel.TryCreate", StringComparison.Ordinal))
            .ToArray();

        samples.Should().HaveCountGreaterThan(
            5, "the search must actually be finding the samples, not silently matching none");

        samples
            .Where(s => !s.Source.Contains("RealModel.PrintModelBanner", StringComparison.Ordinal))
            .Select(s => Path.GetFileName(Path.GetDirectoryName(s.Path))!)
            .Should().BeEmpty("a sample that resolves a provider must say which host answered");
    }

    /// <summary>And every one of them sets the store's vector width from that model.</summary>
    /// <remarks>
    /// The same shape of gap, in the place where it is expensive rather than merely confusing: a
    /// sample that leaves <c>EmbeddingDimensions</c> at the 1536 default while running a 1024-wide
    /// model builds an index that cannot match its own writes, and nothing errors. Only samples that
    /// configure a Neo4j store are checked — NamsAgent uses the hosted backend and has none.
    /// </remarks>
    [Fact]
    public void EverySampleWithAStoreSetsItsWidthFromTheModel()
    {
        var samples = Directory
            .EnumerateFiles(FindRepoDirectory("samples"), "Program.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Source: File.ReadAllText(path)))
            .Where(s => s.Source.Contains("RealModel.TryCreate", StringComparison.Ordinal)
                        && s.Source.Contains("AddNeo4jAgentMemory", StringComparison.Ordinal))
            .ToArray();

        samples.Should().NotBeEmpty("the search must actually be finding the store-backed samples");

        samples
            .Where(s => !s.Source.Contains("EmbeddingDimensions = modelSettings.EmbeddingDimensions",
                StringComparison.Ordinal))
            .Select(s => Path.GetFileName(Path.GetDirectoryName(s.Path))!)
            .Should().BeEmpty(
                "the store's vector width must come from the resolved model, not from a default that "
                + "happened to match the only provider this repository used to support");
    }

    private static string FindRepoDirectory(string relativePath)
    {
        var anchor = Path.Combine(
            "samples", "AgentMemory.Sample.AgentWithMemory", "Program.cs");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, anchor)))
            {
                return Path.Combine(dir.FullName, relativePath);
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate repository directory '{relativePath}'.");
    }
}
