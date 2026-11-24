#pragma warning disable AMEXT001
using AgentMemory.Packs.CodeMap;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// CO-2f, test bed (a): a Rust project marks each test with the requirement it validates, <c>#[spec_test(REQ_CF_001)]</c>, an
/// identifier for the id REQ-CF-001. The code map reads Rust through SCIP, which carries no attributes, so a small text reader
/// gives each marked test function, its line, and the requirement ids it validates, for <see cref="CodeMapBuilder.AddValidates"/>.
/// </summary>
public sealed class RustSpecTestReaderTests
{
    private const string Source = """
        use super::*;

        #[spec_test(REQ_PTR_parser_001)]
        fn pointer_declaration_is_read() {
            let source = "#[spec_test(REQ_NOT_a_test_999)] inside a string";
        }

        #[spec_test(REQ_PTR_core_002)]
        #[spec_test(REQ_PTR_core_003)]
        #[rstest]
        #[ignore]
        pub fn pointer_to_pointer_is_refused(#[case] input: &str) {}

        // #[spec_test(REQ_COMMENTED_out_004)]
        fn not_marked() {}

        #[test]
        fn unmarked_test() {}
        """;

    [Fact]
    public void Each_marked_test_gives_its_function_line_and_requirement_ids()
    {
        var tests = RustSpecTestReader.Read("compiler/src/pointers.rs", Source);

        tests.Should().HaveCount(2);
        tests[0].Function.Should().Be("pointer_declaration_is_read");
        tests[0].Line.Should().Be(4);
        tests[0].Requirements.Should().Equal("REQ-PTR-parser-001");
        tests[1].Function.Should().Be("pointer_to_pointer_is_refused");
        tests[1].Line.Should().Be(12);
        tests[1].Requirements.Should().Equal("REQ-PTR-core-002", "REQ-PTR-core-003");
        tests.Should().OnlyContain(t => t.File == "compiler/src/pointers.rs");
    }

    [Theory]
    [InlineData("REQ_CF_001", "REQ-CF-001")]
    [InlineData("REQ_CB_analyzer_003", "REQ-CB-analyzer-003")]
    [InlineData("REQ_12", "REQ-12")]
    public void A_requirement_identifier_reads_as_its_id(string identifier, string id) =>
        RustSpecTestReader.RequirementId(identifier).Should().Be(id);
}
