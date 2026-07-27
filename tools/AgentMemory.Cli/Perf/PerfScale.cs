namespace AgentMemory.Cli.Perf;

/// <summary>Dataset tier used by the hermetic turn harness.</summary>
public enum PerfScale
{
    Small,
    Medium,
}

public static class PerfScaleParser
{
    public static PerfScale Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "S", StringComparison.OrdinalIgnoreCase))
        {
            return PerfScale.Small;
        }

        if (string.Equals(value, "M", StringComparison.OrdinalIgnoreCase))
            return PerfScale.Medium;

        throw new ArgumentException(
            $"invalid --scale '{value}'; expected 'S' or 'M'.",
            nameof(value));
    }

    public static string Name(this PerfScale scale) => scale switch
    {
        PerfScale.Small => "S",
        PerfScale.Medium => "M",
        _ => throw new ArgumentOutOfRangeException(nameof(scale), scale, null),
    };
}
