namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// How precisely a stored date was stated: "in 2024", "since March 2024", "on 12 March 2024".
/// </summary>
/// <remarks>
/// A validity date is stored as an instant, and an instant cannot say which of those it came from: "March 2024"
/// is stored as 2024-03-01, the same instant as "1 March 2024". Rendering it as a day would state a day nobody
/// said, and an agent then answers "you moved on 1 March". The precision is recorded where the date is read
/// (the extractor knows whether it read a year, a month or a day) and a renderer prints the date at it.
/// </remarks>
public enum DatePrecision
{
    /// <summary>Not recorded (dates written before precision was, or by a writer that does not say). Rendered as a day.</summary>
    Unspecified = 0,

    /// <summary>A year: "in 2024".</summary>
    Year = 1,

    /// <summary>A month: "since March 2024".</summary>
    Month = 2,

    /// <summary>A day: "on 12 March 2024".</summary>
    Day = 3,

    /// <summary>A time of day: "at 17:00 on 12 March 2024".</summary>
    Instant = 4,
}
