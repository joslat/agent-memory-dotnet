using System.Reflection;
using AgentMemory.McpServer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentMemory.Tests.Unit.McpServer;

/// <summary>
/// <c>--read-only</c> must be a fact about the server, not a promise about it.
/// </summary>
/// <remarks>
/// <para>
/// A read-only server that still exposes one write tool is <b>worse</b> than one with no such mode:
/// the operator believes the guarantee and stops checking. The failure is also silent by nature —
/// nothing errors until a model calls the tool and the store changes.
/// </para>
/// <para>
/// So the guard here is not "the current list is right" — a list can be re-read — but that <b>no tool
/// can escape classification</b>. A tool added next year, with nobody thinking about read-only mode,
/// must fail this test rather than quietly ship as callable.
/// </para>
/// </remarks>
public sealed class ReadOnlyModeTests
{
    /// <summary>Every tool name the server actually declares, read from the attributes.</summary>
    /// <remarks>
    /// Reflected rather than listed, because a hand-maintained inventory is precisely the thing that
    /// drifts. Reading the attributes means this test sees what the server exposes, not what someone
    /// remembered to write down.
    /// </remarks>
    private static IReadOnlyList<string> DeclaredToolNames()
    {
        var names = typeof(AgentMemoryMcpOptions).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        names.Should().NotBeEmpty("the reflection above must actually find the tools, or this whole "
            + "test class passes vacuously and guards nothing");
        return names;
    }

    [Fact]
    public void EveryDeclaredToolIsClassifiedAsExactlyOneOfReadOrWrite()
    {
        // THE guard. A tool in neither set is a tool nobody classified; a tool in both is a
        // contradiction. Either way the read-only guarantee stops meaning anything, and neither shows
        // up at runtime.
        foreach (var name in DeclaredToolNames())
        {
            var isRead = McpToolAccess.ReadTools.Contains(name);
            var isWrite = McpToolAccess.WriteTools.Contains(name);

            (isRead ^ isWrite).Should().BeTrue(
                $"'{name}' must be classified as exactly one of read or write in McpToolAccess "
                + "(read={0}, write={1}); an unclassified tool silently decides whether --read-only "
                + "is true", isRead, isWrite);
        }
    }

    [Fact]
    public void TheClassificationNamesNoToolThatDoesNotExist()
    {
        // The other drift direction: a renamed or removed tool leaving a stale entry behind. Harmless
        // on its own, but it makes the lists untrustworthy, and these lists are the only statement of
        // what a read-only server can do.
        var declared = DeclaredToolNames().ToHashSet(StringComparer.Ordinal);

        McpToolAccess.ReadTools.Concat(McpToolAccess.WriteTools)
            .Should().OnlyContain(name => declared.Contains(name));
    }

    [Fact]
    public void AnUnknownToolIsTreatedAsAWrite()
    {
        // Fail closed. A tool this classification has never heard of is one nobody has reviewed, and
        // the safe reading of "unreviewed" is "assume it changes something".
        McpToolAccess.IsReadOnly("memory_do_something_new").Should().BeFalse();
        McpToolAccess.IsReadOnly(null).Should().BeFalse();
        McpToolAccess.IsReadOnly("").Should().BeFalse();
    }

    [Theory]
    [InlineData("memory_store_message")]
    [InlineData("memory_add_fact")]
    [InlineData("memory_invalidate")]
    [InlineData("memory_supersede")]
    [InlineData("extract_and_persist")]
    [InlineData("memory_generate_embeddings")]
    public void TheObviousWritesAreWrites(string name) =>
        McpToolAccess.IsReadOnly(name).Should().BeFalse();

    [Fact]
    public void GraphQueryIsWithheldFromReadOnlyServersDespiteBeingNominallyARead()
    {
        // It takes arbitrary Cypher. It is gated behind EnableGraphQuery and validated read-only at
        // execution, but a mode whose entire value is "this cannot change anything" must not rest on a
        // second component's parser.
        McpToolAccess.IsReadOnly("graph_query").Should().BeFalse();
    }

    [Theory]
    [InlineData("memory_search")]
    [InlineData("memory_get_context")]
    [InlineData("memory_get_conversation")]
    [InlineData("memory_list_sessions")]
    [InlineData("memory_get_entity")]
    [InlineData("memory_get_observations")]
    public void TheReadsSurviveReadOnlyMode(string name)
    {
        // The other half: a read-only server that exposes nothing useful is a mode nobody will turn
        // on, and the point is to make the safe configuration the attractive one.
        McpToolAccess.IsReadOnly(name).Should().BeTrue();
    }

    [Fact]
    public void ReadOnlyIsOffByDefault()
    {
        new AgentMemoryMcpOptions().ReadOnly.Should().BeFalse();
    }

    // ── the filter, on a real registration ───────────────────────────────

    private static IReadOnlyList<string> RegisteredToolNames(bool readOnly)
    {
        var services = new ServiceCollection();
        services.AddMcpServer().AddAgentMemoryMcpTools(options =>
        {
            options.ReadOnly = readOnly;
            options.EnableGraphQuery = true;
        });

        // The SDK registers tools as singleton factories rather than instances, so the name is only
        // readable by invoking one. Reading ImplementationInstance instead returned null for every
        // descriptor -- which made the first version of the production filter a silent no-op that this
        // test caught.
        using var empty = new ServiceCollection().BuildServiceProvider();
        return services
            .Where(descriptor => descriptor.ServiceType == typeof(McpServerTool))
            .Select(descriptor => (descriptor.ImplementationFactory?.Invoke(empty) as McpServerTool)
                ?.ProtocolTool.Name)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToList();
    }

    [Fact]
    public void ReadOnlyRegistrationExposesTheReadsAndNothingElse()
    {
        // Asserted on the registration rather than on the classification lists: the lists could be
        // perfect while the filter never ran, and the server would look exactly the same until a model
        // called a write tool.
        var exposed = RegisteredToolNames(readOnly: true);

        exposed.Should().NotBeEmpty("a read-only server that exposes nothing is a mode nobody turns on");
        exposed.Should().OnlyContain(name => McpToolAccess.ReadTools.Contains(name));
        exposed.Should().NotContain("graph_query",
            "it is withheld even with EnableGraphQuery set, because a mode guaranteeing 'nothing "
            + "changes' must not rest on a query parser");
    }

    [Fact]
    public void TheDefaultRegistrationIsUnchangedAndExposesEverything()
    {
        // The byte-identical guarantee for every existing host: read-only is opt-in, and turning it
        // off must leave the server exactly as it was.
        var exposed = RegisteredToolNames(readOnly: false);

        exposed.Should().BeEquivalentTo(DeclaredToolNames());
    }

    [Fact]
    public void MostToolsAreWritesWhichIsWhyEnumeratingWritesIsTheSaferChoiceToDocument()
    {
        // Recorded as a fact rather than an opinion: the write set is the larger one, so "list the
        // reads and treat the rest as writes" would have been the shorter list. It is still the wrong
        // one, because forgetting to add to it exposes a write instead of hiding a read.
        McpToolAccess.WriteTools.Count.Should().BeGreaterThan(McpToolAccess.ReadTools.Count);
    }
}
