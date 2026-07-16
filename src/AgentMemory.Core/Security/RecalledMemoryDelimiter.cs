namespace AgentMemory.Core.Security;

/// <summary>
/// Delimits and escapes untrusted recalled content (an entity/fact/preference/trace/GraphRAG block, which
/// may originate from a user, an external document, a tool result, or the model itself) so it cannot
/// masquerade as an unrestricted, undelimited instruction (#92 Phase 1). Lives in Core so both the Agent
/// Framework and Semantic Kernel adapters share one implementation (#92 Phase 6) rather than each carrying
/// its own copy -- <c>InternalsVisibleTo</c> already grants both adapters visibility into Core's internals.
/// </summary>
internal static class RecalledMemoryDelimiter
{
    /// <summary>
    /// Wraps <paramref name="content"/> in a <c>&lt;recalled_memory category="..."&gt;</c> boundary, with
    /// every angle bracket in the content escaped so it can never contain a literal
    /// <c>&lt;recalled_memory&gt;</c>/<c>&lt;/recalled_memory&gt;</c> (or any other tag) -- content can
    /// therefore never prematurely close its own boundary or forge a nested one, the same principle as
    /// HTML-encoding user content before embedding it in markup. This defeats boundary <em>forgery</em>
    /// specifically -- it does not detect or block instruction-like content that never relies on the tag
    /// (e.g. plain-language "ignore previous instructions"); pair it with a trusted framing instruction and,
    /// where richer detection is warranted, <see cref="InstructionLikeContentDetector"/>.
    /// </summary>
    public static string Wrap(string category, string content) =>
        $"""<recalled_memory category="{category}">{Escape(content)}</recalled_memory>""";

    private static string Escape(string content) =>
        content.Replace("<", "&lt;").Replace(">", "&gt;");
}
