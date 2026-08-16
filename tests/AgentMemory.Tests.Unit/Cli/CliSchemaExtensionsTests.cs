using AgentMemory.Cli;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Cli;

/// <summary>
/// The operator path for extension DDL: <c>agentmemory migrate --extensions &lt;id,…&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> Extensions ship their schema in <c>ext/&lt;id&gt;/000N</c> scripts, and
/// <c>MigrationRunner</c> applies exactly the ones <see cref="Neo4jOptions.Extensions"/> names. The CLI
/// host set the URI, credentials, database and embedding dimensions — and never touched
/// <c>Extensions</c>. So the one command an operator runs to migrate a database applied base migrations
/// only, and no supported path existed for the extension DDL at all: a host could enable
/// <c>arithmetic</c> in code and then hit a live graph missing <c>fact_derivation_key_idx</c>, which
/// degrades to a full scan rather than an error and is therefore invisible.
/// </para>
/// <para>
/// The flag name is <c>--extensions</c> because <c>schema-parity</c> already used it and the TCK bridge
/// already used it. One name, one meaning, on every surface that has the concept — the alternative is a
/// flag that silently does nothing on two of three verbs.
/// </para>
/// </remarks>
public sealed class CliSchemaExtensionsTests
{
    private static Neo4jOptions Applied(string? argument)
    {
        var options = new Neo4jOptions();
        CliSchemaExtensions.Apply(options, argument);
        return options;
    }

    // ── parsing ───────────────────────────────────────────────────────

    [Fact]
    public void ACommaSeparatedListActivatesEachExtension()
    {
        Applied("arithmetic,delta-recall").Extensions
            .Should().BeEquivalentTo(["arithmetic", "delta-recall"]);
    }

    [Fact]
    public void WhitespaceAroundIdsIsTolerated()
    {
        // An operator pasting from a doc gets "arithmetic, delta-recall". Rejecting that would be a
        // usability failure dressed as strictness, and the ids themselves never contain spaces.
        Applied(" arithmetic , delta-recall ").Extensions
            .Should().BeEquivalentTo(["arithmetic", "delta-recall"]);
    }

    [Fact]
    public void ASingleIdWorks()
    {
        Applied("procedural").Extensions.Should().BeEquivalentTo(["procedural"]);
    }

    [Fact]
    public void EmptyEntriesAreDropped()
    {
        Applied("arithmetic,,delta-recall,").Extensions
            .Should().BeEquivalentTo(["arithmetic", "delta-recall"]);
    }

    // ── the off state ─────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoFlagLeavesTheBaseScheduleUntouched(string? argument)
    {
        // Byte-identical to what `migrate` did before this existed. An operator who does not ask for
        // extensions must get exactly the base sequence, or every existing deployment's next migrate
        // run silently gains schema nobody requested.
        Applied(argument).Extensions.Should().BeEmpty();
    }

    [Fact]
    public void ApplyingNothingDoesNotDisturbExtensionsAlreadyConfigured()
    {
        // Configuration binding and appsettings can populate Extensions before the CLI override runs.
        // An absent flag means "no override", not "clear whatever the host configured" -- the latter
        // would make the CLI silently narrower than the library it drives.
        var options = new Neo4jOptions();
        options.Extensions.Add("procedural");

        CliSchemaExtensions.Apply(options, null);

        options.Extensions.Should().BeEquivalentTo(["procedural"]);
    }

    [Fact]
    public void AnExplicitListReplacesRatherThanAddsToWhatWasConfigured()
    {
        // The flag is the operator's statement of what this run should activate. Merging would make
        // "--extensions arithmetic" mean "arithmetic AND whatever appsettings said", which is not what
        // anyone typing it intends and cannot be un-said from the command line.
        var options = new Neo4jOptions();
        options.Extensions.Add("procedural");

        CliSchemaExtensions.Apply(options, "arithmetic");

        options.Extensions.Should().BeEquivalentTo(["arithmetic"]);
    }

    // ── unknown ids ───────────────────────────────────────────────────

    [Fact]
    public void AnUnknownIdIsRejectedWithTheKnownOnesListed()
    {
        // Parse-time, not migrate-time. The registry already refuses an unknown id when the runner
        // resolves activation -- "a deployment that asked for an extension and silently ran without it
        // is the failure this mechanism exists to make impossible" -- but that surfaces as an exception
        // from inside host construction. Failing here lets the operator see the typo and the list.
        var act = () => Applied("aritmetic");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*aritmetic*")
            .WithMessage("*arithmetic*", "the message must name the known ids, or a typo is a dead end");
    }

    [Fact]
    public void TheKnownIdsAreTheShippedOnes()
    {
        CliSchemaExtensions.KnownIds.Should().BeEquivalentTo(
            ["arithmetic", "delta-recall", "procedural", "working-memory"]);
    }

    // ── the middle link ───────────────────────────────────────────────

    [Fact]
    public void TheCliHostActuallyAppliesTheParsedExtensions()
    {
        // Parsing the flag and honouring the option are two ends of a chain with a third piece between
        // them, and that piece is where this whole finding lived: Neo4jOptions.Extensions was settable,
        // MigrationRunner read it, and the CLI host never assigned it. A guard on either end alone
        // would have stayed green through the entire gap.
        //
        // Source inspection with comment lines stripped, because a substring assertion over raw source
        // is satisfied by a commented-out occurrence -- which is the likeliest way this wire gets cut,
        // and which left an earlier guard in this repository green through exactly that probe.
        var source = LiveLines(ProgramSource());

        source.Should().Contain("CliSchemaExtensions.Parse(extensionsArg)");
        source.Should().Contain("o.Extensions = new HashSet<string>(activeExtensions");
    }

    [Fact]
    public void TheHostResolvesExtensionsThroughTheSamePrecedenceAsEveryOtherSetting()
    {
        // CLI option > Neo4j:* config > NEO4J_* env > default. An operator who can set the URI from
        // appsettings but must set extensions on the command line has two configuration systems for
        // one deployment.
        LiveLines(ProgramSource()).Should().Contain(
            "Resolve(\"extensions\", string.Empty, \"Neo4j:Extensions\", \"NEO4J_EXTENSIONS\")");
    }

    /// <summary>The source with comment lines stripped.</summary>
    private static string LiveLines(string source) =>
        string.Join(
            '\n',
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string ProgramSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx")))
            directory = directory.Parent;
        directory.Should().NotBeNull("the test must run inside the repository");

        return File.ReadAllText(
            Path.Combine(directory!.FullName, "tools", "AgentMemory.Cli", "Program.cs"));
    }

    [Fact]
    public void IdsAreCaseSensitiveBecauseTheyKeyMigrationBookkeeping()
    {
        // An id is part of the (:Migration).version key -- "ext/arithmetic/0001". Accepting
        // "Arithmetic" and storing "arithmetic" would work; accepting it and storing it verbatim would
        // orphan every previously-applied row. Refusing is the only option that cannot silently split
        // one extension's history in two.
        var act = () => Applied("Arithmetic");

        act.Should().Throw<ArgumentException>();
    }
}
