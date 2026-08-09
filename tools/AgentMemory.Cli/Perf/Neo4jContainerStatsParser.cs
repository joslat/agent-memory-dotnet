using System.Globalization;
using System.Text.Json;

namespace AgentMemory.Cli.Perf;

internal readonly record struct Neo4jContainerStatsSample(
    double CpuRawPercent,
    double CpuCapacityPercent,
    long MemoryUsedBytes,
    long MemoryLimitBytes,
    double MemoryPercent,
    long BlockReadBytes,
    long BlockWriteBytes,
    long ProcessCount);

internal static class Neo4jContainerStatsParser
{
    public static bool TryParse(
        string json,
        double effectiveCpuCount,
        out Neo4jContainerStatsSample sample)
    {
        sample = default;
        if (string.IsNullOrWhiteSpace(json) || effectiveCpuCount <= 0)
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!TryPercent(root, "CPUPerc", out var cpuRawPercent) ||
                !TrySplitBytes(root, "MemUsage", out var memoryUsedBytes, out var memoryLimitBytes) ||
                !TryPercent(root, "MemPerc", out var memoryPercent) ||
                !TrySplitBytes(root, "BlockIO", out var blockReadBytes, out var blockWriteBytes) ||
                !root.TryGetProperty("PIDs", out var pidsElement) ||
                !long.TryParse(
                    pidsElement.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var processCount))
            {
                return false;
            }

            sample = new Neo4jContainerStatsSample(
                cpuRawPercent,
                cpuRawPercent / effectiveCpuCount,
                memoryUsedBytes,
                memoryLimitBytes,
                memoryPercent,
                blockReadBytes,
                blockWriteBytes,
                processCount);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseBytes(string text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var index = 0;
        while (index < text.Length &&
               (char.IsDigit(text[index]) || text[index] is '.' or ',' or '+' or '-'))
        {
            index++;
        }

        if (index == 0 ||
            !double.TryParse(
                text[..index].Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value) ||
            value < 0)
        {
            return false;
        }

        var unit = text[index..].Trim();
        var multiplier = unit switch
        {
            "B" => 1d,
            "kB" => 1_000d,
            "MB" => 1_000_000d,
            "GB" => 1_000_000_000d,
            "TB" => 1_000_000_000_000d,
            "KiB" => 1_024d,
            "MiB" => 1_048_576d,
            "GiB" => 1_073_741_824d,
            "TiB" => 1_099_511_627_776d,
            _ => double.NaN,
        };
        if (double.IsNaN(multiplier) || value > long.MaxValue / multiplier)
            return false;

        bytes = (long)(value * multiplier);
        return true;
    }

    private static bool TryPercent(JsonElement root, string property, out double value)
    {
        value = 0;
        return root.TryGetProperty(property, out var element) &&
               element.ValueKind == JsonValueKind.String &&
               double.TryParse(
                   element.GetString()?.TrimEnd('%'),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static bool TrySplitBytes(
        JsonElement root,
        string property,
        out long first,
        out long second)
    {
        first = 0;
        second = 0;
        if (!root.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var parts = element.GetString()?.Split('/', StringSplitOptions.TrimEntries);
        return parts is { Length: 2 } &&
               TryParseBytes(parts[0], out first) &&
               TryParseBytes(parts[1], out second);
    }
}
