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
        $"""<recalled_memory category="{EscapeAttribute(category)}">{Escape(content)}</recalled_memory>""";

    /// <summary>
    /// A record a pack brought, framed with its source (feature 04, R-HOST-053 and 054): <c>&lt;record pack="…" version="…"
    /// type="…" key="…"&gt;text&lt;/record&gt;</c>, every attribute and the text escaped, so a record can neither close its frame
    /// nor change what its header says. Frames stand inside one section's wrapper (<see cref="WrapEscaped"/>).
    /// </summary>
    public static string Frame(IEnumerable<KeyValuePair<string, string>> attributes, string text) =>
        "<record" + string.Concat(attributes.Select(a => $" {a.Key}=\"{EscapeAttribute(a.Value)}\"")) + ">" + Escape(text) + "</record>";

    /// <summary>A text escaped as <see cref="Wrap"/> escapes its content: for a section that holds frames beside plain items.</summary>
    public static string EscapeText(string text) => Escape(text);

    /// <summary>Wraps content already escaped (frames and escaped items) under its category, as <see cref="Wrap"/> does.</summary>
    public static string WrapEscaped(string category, string escaped) =>
        $"""<recalled_memory category="{EscapeAttribute(category)}">{escaped}</recalled_memory>""";

    // THE CATEGORY IS INTERPOLATED INTO AN ATTRIBUTE, so it needs the quote escaped as well as the
    // angle brackets. For years every caller passed a built-in constant and this was unreachable;
    // module-defined section types made the category caller-supplied, and an id containing a quote
    // could then close the attribute and forge wrapper metadata around content that was itself
    // escaped correctly. Escaped here rather than at that one call site, because the precondition
    // "callers pass a trusted category" was never written down and the next caller will not know it.
    private static string EscapeAttribute(string value) =>
        Escape(value).Replace("\"", "&quot;");

    private static string Escape(string content) =>
        content.Replace("<", "&lt;").Replace(">", "&gt;");
}
