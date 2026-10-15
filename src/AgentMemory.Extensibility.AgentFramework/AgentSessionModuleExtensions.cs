using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace AgentMemory.Extensibility.AgentFramework;

/// <summary>Host-selected read times for explicitly registered module contributors.</summary>
[Experimental("AMEXT001")]
public static class AgentSessionModuleExtensions
{
    internal const string SystemTimeKey = "agentmemory.modules.system_as_of";
    internal const string ValidTimeKey = "agentmemory.modules.valid_as_of";

    /// <summary>
    /// Replaces both module read-time selections. Null clears that selection; both null restores
    /// current module reads. Returns the session for chaining. Does not change core recall or writes.
    /// </summary>
    /// <remarks>
    /// Times are stored as UTC round-trip strings in the serializable session state. Unsupported
    /// valid-time reads remain subject to the contributor's capability checks. Malformed persisted
    /// selections refuse module compilation rather than falling back to current data. With no module
    /// contributors registered, the adapter does not read these keys or alter core context.
    /// </remarks>
    public static AgentSession WithModuleReadTime(
        this AgentSession session,
        DateTimeOffset? systemAsOfUtc = null,
        DateTimeOffset? validAsOfUtc = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        SetTime(session, SystemTimeKey, systemAsOfUtc);
        SetTime(session, ValidTimeKey, validAsOfUtc);
        return session;
    }

    internal static (DateTimeOffset? SystemAsOf, DateTimeOffset? AsOf) ReadModuleTimes(AgentSession? session)
        => (ReadTime(session, SystemTimeKey), ReadTime(session, ValidTimeKey));

    private static void SetTime(AgentSession session, string key, DateTimeOffset? value)
    {
        if (value is null)
        {
            session.StateBag.TryRemoveValue(key);
            return;
        }
        session.StateBag.SetValue(key,
            value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            JsonSerializerOptions.Default);
    }

    private static DateTimeOffset? ReadTime(AgentSession? session, string key)
    {
        if (session is null) return null;
        string? raw;
        try
        {
            // GetValue distinguishes an absent key from an incompatible stored value;
            // TryGetValue returns false for both and would silently lose the time selection.
            raw = session.StateBag.GetValue<string>(key, JsonSerializerOptions.Default);
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonException("Invalid module read-time selection shape.", exception);
        }
        if (raw is null) return null;
        if (!DateTimeOffset.TryParseExact(raw, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            throw new JsonException("Invalid module read-time selection.");
        return time.ToUniversalTime();
    }
}
