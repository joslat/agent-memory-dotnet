using AgentMemory.Abstractions.Domain;
using AgentMemory.AgentFramework.Mapping;

namespace AgentMemory.AgentFramework;

public partial class Neo4jMemoryContextProvider
{
    /// <summary>An immutable item admitted by this provider for one category.</summary>
    /// <remarks>Adapters can select these items before rendering without repeating policy evaluation.
    /// This is a decision for the current request, not a durable trust grant or a sandbox for module code.</remarks>
    protected sealed class AdmittedMemoryItem
    {
        internal AdmittedMemoryItem(Neo4jMemoryContextProvider provider, string category, string text)
        {
            Provider = provider;
            Category = category;
            Text = text;
        }

        internal Neo4jMemoryContextProvider Provider { get; }
        /// <summary>The category evaluated by the provider's admission policy.</summary>
        public string Category { get; }
        /// <summary>The exact admitted text, before delimiting and escaping.</summary>
        public string Text { get; }
    }

    /// <summary>Evaluates one untrusted item using this provider's effective policy and security options.</summary>
    /// <returns>An immutable admitted item, or null when refused. Only admitted text may be sent to a judge.</returns>
    /// <remarks>Performs no retrieval or model call. Use only within the current request; retain the returned
    /// item through selection and render it with RenderAdmittedItems without evaluating admission again.</remarks>
    protected AdmittedMemoryItem? AdmitMemoryItem(string category, string text)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(text);
        return AdmitUntrustedText(category, text) ? new(this, category, text) : null;
    }

    /// <summary>Delimits and escapes selected admitted items without invoking the policy again.</summary>
    /// <remarks>Items must originate from this provider and category. Can be repeated to measure exact
    /// rendered UTF-8 bytes before choosing a whole-item subset. Does not truncate items or call a model.</remarks>
    protected string RenderAdmittedItems(string category, IEnumerable<AdmittedMemoryItem> items)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(items);
        var texts = new List<string>();
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!ReferenceEquals(item.Provider, this) || !string.Equals(item.Category, category, StringComparison.Ordinal))
                throw new ArgumentException("Admitted items must belong to this provider and category.", nameof(items));
            texts.Add(item.Text);
        }
        return WrapAdmittedTexts(category, texts);
    }

    /// <summary>
    /// Admits untrusted extension items using this provider's effective policy and security options,
    /// then delimits and escapes them as one recalled-memory block. Returns empty if all items are refused.
    /// </summary>
    /// <remarks>Evaluates each item once. This method performs no retrieval or persistence.
    /// Existing callers do not allocate the two-stage admission items.</remarks>
    protected string RenderAdmittedBlock(string category, IEnumerable<string> texts)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(texts);
        var admitted = new List<string>();
        foreach (var text in texts)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (AdmitUntrustedText(category, text)) admitted.Add(text);
        }
        return WrapAdmittedTexts(category, admitted);
    }

    private bool AdmitUntrustedText(string category, string text) =>
        MafTypeMapper.AdmitItem(category, text, MemoryTrustLevel.Untrusted, _formatOptions, _admissionPolicy, _logger);

    private static string WrapAdmittedTexts(string category, IReadOnlyCollection<string> texts) =>
        texts.Count == 0 ? string.Empty : MafTypeMapper.WrapUntrustedContent(category, string.Join(Environment.NewLine, texts));
}
