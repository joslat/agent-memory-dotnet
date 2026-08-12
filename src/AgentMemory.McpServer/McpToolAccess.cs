namespace AgentMemory.McpServer;

/// <summary>
/// Which of the memory tools change stored state.
/// </summary>
/// <remarks>
/// <para>
/// This exists so <c>--read-only</c> can be a fact rather than a promise. A read-only server that
/// still exposes a write tool is worse than one with no such mode, because the operator believes the
/// guarantee and stops checking.
/// </para>
/// <para>
/// <b>Writes are the enumerated set, deliberately.</b> Listing the reads instead would mean a tool
/// added later and forgotten defaults to "not a write" and stays exposed in read-only mode — the
/// failure lands silently on the safety side. Enumerating writes makes the same forgetfulness fail
/// the other way: the new tool is treated as a write, disappears from read-only servers, and somebody
/// notices. A guard test asserts every registered tool is named in exactly one of the two lists, so
/// the choice is enforced rather than hoped for.
/// </para>
/// <para>
/// <c>graph_query</c> is a write for this purpose even though it is nominally a read: it takes
/// arbitrary Cypher. It is already gated behind <c>EnableGraphQuery</c> and validated read-only at
/// execution, but a mode whose entire value is "this cannot change anything" should not rest on a
/// second component's parser.
/// </para>
/// </remarks>
public static class McpToolAccess
{
    /// <summary>Tools that create, modify, invalidate or derive stored memory.</summary>
    public static IReadOnlySet<string> WriteTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "memory_store_message",
        "memory_add_entity",
        "memory_add_preference",
        "memory_add_fact",
        "memory_create_relationship",
        "memory_record_entity_feedback",
        "memory_start_trace",
        "memory_record_step",
        "memory_complete_trace",
        "memory_record_tool_call",
        "extract_and_persist",
        "memory_extract_session",
        "memory_generate_embeddings",
        "memory_invalidate",
        "memory_supersede",
        // Arbitrary Cypher. Read-only in intent and validated as such at execution, but a mode that
        // exists to guarantee "nothing changes" must not delegate that guarantee to a query parser.
        "graph_query",
    };

    /// <summary>Tools that only read.</summary>
    /// <remarks>
    /// Named explicitly rather than derived as "everything else", so that the guard test can prove no
    /// tool falls outside both sets — which is the only way "read-only" stays true as tools are added.
    /// </remarks>
    public static IReadOnlySet<string> ReadTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "memory_search",
        "memory_get_context",
        "memory_get_conversation",
        "memory_list_sessions",
        "memory_get_entity",
        "memory_get_entity_provenance",
        "memory_get_observations",
        "memory_export_graph",
        "memory_find_duplicates",
    };

    /// <summary>Whether <paramref name="toolName"/> may run on a read-only server.</summary>
    /// <remarks>
    /// An unrecognised name is treated as a write. A tool this class has never heard of is a tool
    /// nobody has classified, and the safe reading of "unclassified" is "assume it changes something".
    /// </remarks>
    public static bool IsReadOnly(string? toolName) =>
        toolName is not null && ReadTools.Contains(toolName);
}
