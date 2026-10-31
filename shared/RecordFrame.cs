// One source compiled into Core (the provider's delimiter) and Extensibility (the trial's renderer), as PackRegistry.cs is
// shared: the bytes a trial measures are the bytes the model reads (feature 05), with no copy of the frame.
#if AGENTMEMORY_CORE
namespace AgentMemory.Core.Security;
#else
namespace AgentMemory.Extensibility;
#endif

/// <summary>
/// The frame a record reaches a model in (feature 04, R-HOST-053 and 054): <c>&lt;record a="…" …&gt;text&lt;/record&gt;</c>, its
/// attributes and text escaped, so a record can neither close its frame nor change what its header says.
/// </summary>
internal static class RecordFrame
{
    /// <summary>A record's frame: its attributes in order, then its text, each escaped.</summary>
    internal static string Frame(IEnumerable<KeyValuePair<string, string>> attributes, string text) =>
        "<record" + string.Concat(attributes.Select(a => $" {a.Key}=\"{EscapeAttribute(a.Value)}\"")) + ">" + Escape(text) + "</record>";

    /// <summary>Text as a frame holds it: every angle bracket escaped, so it opens and closes no tag.</summary>
    internal static string Escape(string content) => content.Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>An attribute's value as a frame holds it: escaped as text, and its double quote too.</summary>
    internal static string EscapeAttribute(string value) => Escape(value).Replace("\"", "&quot;");
}
