using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AgentMemory.Native.Composition;

namespace AgentMemory.Native.Probe;

/// <summary>
/// Feasibility probe: one export that runs a real read-only recall, so the
/// compiler analyses the actual recall path rather than an empty library. Settings come from the environment
/// (AMP_URI, AMP_USER, AMP_PASSWORD, AMP_OWNER, AMP_DIMS, AMP_VECTOR as comma-separated numbers). It is replaced by
/// the versioned C ABI and is not a supported entry point.
/// </summary>
internal static class ProbeExport
{
    [UnmanagedCallersOnly(EntryPoint = "am_probe_recall", CallConvs = [typeof(CallConvCdecl)])]
    public static int ProbeRecall()
    {
        try
        {
            var settings = new MemorySettings
            {
                Uri = Required("AMP_URI"),
                Username = Required("AMP_USER"),
                Password = Required("AMP_PASSWORD"),
                OwnerId = Required("AMP_OWNER"),
                EmbeddingDimensions = int.Parse(Required("AMP_DIMS"), CultureInfo.InvariantCulture),
            };
            var vector = Required("AMP_VECTOR")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => float.Parse(v, CultureInfo.InvariantCulture))
                .ToArray();

            var provider = MemoryComposition.Build(settings);
            Abstractions.Domain.MemoryContext context;
            try
            {
                context = MemoryComposition.RecallAsync(
                        provider,
                        settings.OwnerId,
                        "speech-probe",
                        vector,
                        MemoryComposition.ReadOnlyRecall(settings.OwnerId, maxFacts: 5, maxPreferences: 5, minScore: 0.0),
                        CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            finally
            {
                provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            Console.Out.WriteLine(
                $"facts={context.RelevantFacts.Items.Count} preferences={context.RelevantPreferences.Items.Count} " +
                $"budgetExceeded={context.LatencyBudgetExceeded}");
            foreach (var fact in context.RelevantFacts.Items)
                Console.Out.WriteLine($"fact {fact.FactId} {fact.Subject} {fact.Predicate} {fact.Object} metadata={fact.Metadata.Count}");
            foreach (var preference in context.RelevantPreferences.Items)
                Console.Out.WriteLine($"preference {preference.PreferenceId} {preference.PreferenceText} metadata={preference.Metadata.Count}");
            Console.Out.Flush();
            return context.RelevantFacts.Items.Count + context.RelevantPreferences.Items.Count;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"probe failed: {exception}");
            Console.Error.Flush();
            return -1;
        }
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable {name} is not set.");
}
