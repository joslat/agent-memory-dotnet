using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// PACK-strict (review 2.2; v1.1 §5 rule 1): signed metadata holds only the fields the format defines. An unknown field in
/// pack.json, a lock or members.json refuses the pack; what the format does not define goes in <c>extensions</c>, which is
/// ignored unless the pack marks an extension critical, and a critical extension this reader does not understand refuses it.
/// </summary>
public sealed class StrictFieldsTests
{
    private const string Head = "\"format\":\"agentmemory.pack/1.2\",\"id\":\"io.example.notes\",\"version\":\"1.0.0\",\"kind\":\"knowledge\"";

    private static (PackManifest? Manifest, string? Problem) Parse(string rest) =>
        PackManifest.Parse(Encoding.UTF8.GetBytes("{" + Head + rest + "}"));

    [Fact]
    public void Every_field_the_format_defines_is_accepted()
    {
        var (manifest, problem) = Parse(""","sdk":"[0.1,0.2)","requires":{},"display":{"title":"Notes"},"containableBy":[],"contains":[],"tier":"open","licence":"MIT","validUntil":"2030-01-01T00:00:00Z","protection":"open","code":"E0","source":{"commit":"0a1b2c3"},"extensions":{}""");

        problem.Should().BeNull();
        manifest!.Id.Should().Be("io.example.notes");
    }

    [Fact]
    public void An_unknown_field_refuses_the_pack_and_names_the_field()
    {
        Parse(""","homepage":"https://example.com" """).Problem.Should().Contain("unknown field 'homepage'").And.Contain("extensions");
        Parse(""","Kind":"knowledge" """).Problem.Should().Contain("unknown field 'Kind'", "field names are exact");
    }

    [Fact]
    public void An_extension_is_ignored_unless_it_is_critical()
    {
        Parse(""","extensions":{"io.example.cache":{"ttl":3600}}""").Problem.Should().BeNull();
        Parse(""","extensions":{"io.example.cache":{"ttl":3600},"critical":["io.example.cache"]}""").Problem
            .Should().Contain("critical").And.Contain("does not understand");
    }

    [Theory]
    [InlineData(""","extensions":[]""", "malformed 'extensions'")]
    [InlineData(""","extensions":{"critical":"io.example.cache"}""", "malformed 'extensions.critical'")]
    [InlineData(""","extensions":{"critical":[1]}""", "malformed 'extensions.critical'")]
    [InlineData(""","extensions":{"a":1,"critical":["a","a"]}""", "twice")]
    [InlineData(""","extensions":{"critical":["io.example.absent"]}""", "no such extension")]
    [InlineData(""","extensions":{"critical":["critical"]}""", "no such extension")]
    public void A_malformed_extensions_object_is_refused(string rest, string why) =>
        Parse(rest).Problem.Should().Contain(why);

    [Fact]
    public void A_lock_or_a_member_entry_with_an_unknown_field_is_refused()
    {
        const string digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        byte[] Lock(string top, string member) => Jcs.Canonical(Encoding.UTF8.GetBytes(
            "{\"format\":\"" + PackLock.Format + "\",\"members\":[{\"digest\":\"" + digest + "\",\"id\":\"io.example.notes\",\"version\":\"1.0.0\"" + member + "}]" + top + "}"));

        PackLock.Parse(Lock("", "")).Problem.Should().BeNull();
        PackLock.Parse(Lock(",\"note\":\"x\"", "")).Problem.Should().Contain("nothing else");
        PackLock.Parse(Lock("", ",\"priority\":1")).Problem.Should().Contain("malformed member");
    }

    [Fact]
    public void Members_json_holds_members_only()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = Encoding.UTF8.GetBytes("{\"format\":\"agentmemory.pack/1.2\",\"id\":\"io.example.family\",\"version\":\"1.0.0\",\"kind\":\"composite\"}"),
            ["members.json"] = Encoding.UTF8.GetBytes("{\"members\":[],\"order\":\"alphabetical\"}"),
        };

        FluentActions.Invoking(() => PackLayout.ToComposite(files)).Should().Throw<InvalidDataException>().WithMessage("*unknown field 'order'*");
    }
}
