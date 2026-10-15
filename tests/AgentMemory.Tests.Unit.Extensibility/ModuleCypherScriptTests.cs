using AgentMemory.Extensibility.Neo4j.Migrations;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleCypherScriptTests
{
    [Fact]
    public void QuotedSemicolonsAndUrlSlashesAreData()
    {
        var source = "MATCH (n:ReviewItem) SET n.url = 'https://example.org/a;b'; MATCH (n:ReviewItem) SET n.note = \"a;b\";";
        ModuleCypherScript.Split(source).Should().Equal(
            "MATCH (n:ReviewItem) SET n.url = 'https://example.org/a;b'",
            "MATCH (n:ReviewItem) SET n.note = \"a;b\"");
    }

    [Fact]
    public void CommentsCannotCreateExtraStatementsOrJoinKeywords()
    {
        ModuleCypherScript.Split("// heading;\nMATCH/* note; */(n:ReviewItem) SET n.x = 1; /* tail; */")
            .Should().Equal("MATCH (n:ReviewItem) SET n.x = 1");
    }

    [Theory]
    [InlineData("MATCH (n:ReviewItem) SET n.x = 'unfinished")]
    [InlineData("MATCH (n:ReviewItem) SET n.x = \"unfinished")]
    [InlineData("MATCH (n:`ReviewItem)")]
    [InlineData("/* unfinished")]
    public void IncompleteInputIsRefusedBeforeAnyExecution(string source)
    {
        var action = () => ModuleCypherScript.Split(source);
        action.Should().Throw<FormatException>();
    }

    [Fact]
    public void NestedCommentsDoNotExposeHiddenStatements()
    {
        ModuleCypherScript.Split("/* outer /* inner; */ still comment; */ MATCH (n:ReviewItem) RETURN n;")
            .Should().Equal("MATCH (n:ReviewItem) RETURN n");
    }

    [Fact]
    public void EscapesAndQuotedIdentifiersPreserveExecutableBytes()
    {
        var source = "MATCH (n:`Review``;Item`) SET n.note = 'can\\'t; // split', n.other = 'a\\\\';";
        ModuleCypherScript.Split(source).Should().Equal(source[..^1]);
        ModuleCypherScript.Parse(source)[0].Tokens.Should().Contain(new ModuleCypherToken(
            ModuleCypherTokenKind.QuotedIdentifier, "Review`;Item"));
    }

    [Fact]
    public void LiteralContentCannotBecomeAnOperationOrNamespaceToken()
    {
        var statement = ModuleCypherScript.Parse("MATCH (n:ReviewItem) SET n.x = 'DELETE n:Fact;';")[0];
        statement.Tokens.Should().Contain(new ModuleCypherToken(ModuleCypherTokenKind.Literal, "DELETE n:Fact;"));
        statement.Tokens.Should().NotContain(token => token.Kind == ModuleCypherTokenKind.Word && token.Text == "DELETE");
    }

    [Theory]
    [InlineData("")]
    [InlineData("; ;; \r\n // comment")]
    [InlineData("/* comment */")]
    public void EmptyAndCommentOnlyScriptsContainNoStatements(string source) =>
        ModuleCypherScript.Split(source).Should().BeEmpty();

    [Theory]
    [InlineData("MATCH (n:ReviewItem]")]
    [InlineData("MATCH (n:ReviewItem")]
    [InlineData("MATCH (n:ReviewItem;)")]
    [InlineData("MATCH (n:`Review\\u0049tem`)")]
    [InlineData("MATCH (n:ReviewItem)\0 RETURN n")]
    public void AmbiguousOrMalformedInputIsRefused(string source)
    {
        var action = () => ModuleCypherScript.Parse(source);
        action.Should().Throw<FormatException>();
    }

    [Fact]
    public void InputAndNestingAreBounded()
    {
        var large = () => ModuleCypherScript.Parse(new string(' ', ModuleCypherScript.MaximumCharacters + 1));
        var deepExpression = () => ModuleCypherScript.Parse(new string('(', 65));
        var deepComment = () => ModuleCypherScript.Parse(string.Concat(Enumerable.Repeat("/*", 65)));
        large.Should().Throw<FormatException>();
        deepExpression.Should().Throw<FormatException>();
        deepComment.Should().Throw<FormatException>();
    }

    [Fact]
    public void ParsingDoesNotAlterWholeArtifactHashes()
    {
        const string original = "// preserved in hash\r\nMATCH (n:ReviewItem) RETURN n;";
        ModuleCypherScript.Parse(original).Should().ContainSingle();
        ModuleMigrationDefinitions.Hash(original).Should().NotBe(
            ModuleMigrationDefinitions.Hash(ModuleCypherScript.Split(original)[0]));
    }
}
