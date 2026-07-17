namespace AgentMemory.Nams.Recall;

/// <summary>Which tier of NAMS-hosted memory a <see cref="NamsRecalledItem"/> came from (engineering plan
/// §7 Phase 4 categories: <c>nams.reflection</c>, <c>nams.observation</c>, <c>nams.recent_message</c>,
/// <c>nams.relevant_message</c>, <c>nams.entity</c>, <c>nams.reasoning</c>).</summary>
public enum NamsRecallCategory
{
    /// <summary>Highest-level compressed insight (<c>nams.reflection</c>).</summary>
    Reflection,

    /// <summary>Mid-level context summary (<c>nams.observation</c>).</summary>
    Observation,

    /// <summary>Raw recent conversation message (<c>nams.recent_message</c>).</summary>
    RecentMessage,

    /// <summary>A message returned by a relevance-driven search (<c>nams.relevant_message</c>). Never
    /// populated by this phase -- degrades to <see cref="RecentMessage"/> since no message-search REST
    /// operation is confirmed to exist (see <c>Client.INamsClient</c>'s dropped <c>SearchMessagesAsync</c>).</summary>
    RelevantMessage,

    /// <summary>An entity returned by a query-driven search (<c>nams.entity</c>).</summary>
    Entity,

    /// <summary>A reasoning-trace item (<c>nams.reasoning</c>). Never populated by this phase -- no
    /// reasoning-trace-retrieval method exists on <c>INamsClient</c> yet.</summary>
    Reasoning
}
