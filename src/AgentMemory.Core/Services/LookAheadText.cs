using System.Globalization;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Services;

/// <summary>The words the look-ahead sections render with, shared by every render surface.</summary>
internal static class LookAheadText
{
    /// <summary>
    /// The label of the time-window section: "In go-live week (2026-12-07 to 2026-12-13)" for an anchored window,
    /// "Between 2026-12-07 and 2026-12-13" for one the question named, "On 2026-12-11" for a single day.
    /// </summary>
    internal static string WindowLabel(TimeWindow? window)
    {
        if (window is null) return "In that period";
        var start = window.StartUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = window.EndUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var range = start == end ? $"on {start}" : $"{start} to {end}";
        if (window.Anchor is { Length: > 0 } anchor)
            return start == end ? $"On {anchor} day ({start})" : $"In {anchor} week ({range})";
        return start == end ? $"On {start}" : $"Between {start} and {end}";
    }
}
