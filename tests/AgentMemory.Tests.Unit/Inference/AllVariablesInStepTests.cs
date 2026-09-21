using System.Text.RegularExpressions;
using AgentMemory.Inference;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Inference;

/// <summary>
/// <see cref="InferenceProviderEnvironment.AllVariables"/> lists everything the resolver reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a list can be wrong without anything failing.</b> The list drives two things that only
/// matter when they are complete: <see cref="InferenceProviderEnvironment.AnyConfigurationAttempted"/>,
/// which decides whether a machine has a typo or no configuration at all; and the test scrubber,
/// which clears the environment before a test runs. A variable missing from it produces no error —
/// it produces a test that passes on CI and fails on the one developer machine that happens to have
/// that variable set, or the reverse.
/// </para>
/// <para>
/// The provider variables are derived from the same specs the resolver reads, so they cannot drift.
/// The blocks that are not — the judge, embedding-override and role variables — are checked here
/// against the resolver's own source, which is the only place a newly added variable can hide.
/// This repository already tests a sample's source for the same kind of reason.
/// </para>
/// </remarks>
public sealed class AllVariablesInStepTests
{
    /// <summary>Every environment-variable-shaped literal in the resolver is in the list.</summary>
    [Fact]
    public void EveryVariableTheResolverNamesIsListed()
    {
        var source = File.ReadAllText(
            FindRepoFile("src/AgentMemory.Inference/InferenceProviderEnvironment.cs"));

        // Environment-variable shape: SHOUTY_SNAKE_CASE of at least two segments. That excludes C#
        // identifiers and prose, and includes every name this contract defines.
        var named = Regex
            .Matches(source, "\"([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        named.Should().NotBeEmpty("the regex must actually be finding the variable names");

        named.Except(InferenceProviderEnvironment.AllVariables, StringComparer.Ordinal)
            .Should().BeEmpty("a variable the resolver reads but does not list is invisible to "
                              + "AnyConfigurationAttempted and to the test scrubber");
    }

    /// <summary>Setting any single listed variable counts as an attempt at configuration.</summary>
    /// <remarks>
    /// The functional half. It is what makes rule 4 — "a half-configured provider is a typo, not an
    /// unchosen one" — able to fire at all.
    /// </remarks>
    [Fact]
    public void AnySingleVariableCountsAsConfigurationAttempted()
    {
        foreach (var variable in InferenceProviderEnvironment.AllVariables)
        {
            InferenceProviderEnvironment
                .AnyConfigurationAttempted(name => name == variable ? "x" : null)
                .Should().BeTrue($"'{variable}' is listed, so setting it is an attempt");
        }
    }

    /// <summary>An empty environment is not an attempt.</summary>
    [Fact]
    public void AnEmptyEnvironmentIsNotAnAttempt()
    {
        InferenceProviderEnvironment.AnyConfigurationAttempted(_ => null).Should().BeFalse();
        InferenceProviderEnvironment.AnyConfigurationAttempted(_ => "   ").Should().BeFalse(
            "whitespace is how an unset variable arrives from a shell script, and it means unset");
    }

    /// <summary>The list has no duplicates, so a scrub or a report cannot double-count.</summary>
    [Fact]
    public void TheListHasNoDuplicates()
    {
        InferenceProviderEnvironment.AllVariables
            .Should().OnlyHaveUniqueItems();
    }

    /// <summary>Every provider in the enum has an operator token, in both directions.</summary>
    /// <remarks>
    /// A provider added to the enum and not to the token table would throw from
    /// <see cref="InferenceProviderNames.ToToken"/> the first time a run identity was stamped —
    /// which is to say, in the middle of a measurement rather than here.
    /// </remarks>
    [Fact]
    public void EveryProviderHasATokenThatRoundTrips()
    {
        foreach (var provider in Enum.GetValues<InferenceProvider>())
        {
            if (provider == InferenceProvider.None) continue;

            var token = InferenceProviderNames.ToToken(provider);
            InferenceProviderNames.TryParse(token, out var parsed).Should().BeTrue();
            parsed.Should().Be(provider);
        }
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.", relativePath);
    }
}
