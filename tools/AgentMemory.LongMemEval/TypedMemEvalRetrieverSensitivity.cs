using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AgentEval.Memory.External.TypedMemEval;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Whether a shape can rank systems <b>for us</b> — read from AgentEval 0.36's
/// <c>probes.retriever_sensitivity</c>, because every published headroom figure is conditioned on a
/// BM25 retriever and <b>this stack retrieves with embeddings</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The condition that was implicit until 0.36.</b> Published headroom is <c>1 − ALLgold</c>
/// measured with <c>bm25-okapi-k1.5-b0.75</c> at <c>K_ref = 5</c>. The sidecar now states that
/// pairing outright and adds the same measurement under a dense retriever over the same documents
/// and budget. A shape marked <c>discriminates_under_dense: false</c> <b>can still rank two systems
/// for a BM25 consumer and cannot for an embedding one</b> — which is us.
/// </para>
/// <para>
/// <b>So such a shape is NON-RANKING FOR US regardless of the score it produces.</b> Reporting a
/// number from it and placing it on a scoreboard would rank systems on a measurement that cannot
/// separate them, which is worse than reporting nothing: the number looks like evidence.
/// </para>
/// <para>
/// <b>This is a PREDICTION from retrieval, not a probe run</b> — their words — and it says nothing
/// about chunking, reranking or query rewriting. It is carried forward as their claim, labelled as
/// theirs, rather than restated as a measurement of ours.
/// </para>
/// <para>
/// Read from the package the run loads and never hardcoded, so a redrawn corpus brings its own
/// sensitivity block. A shape with no entry reads as <b>unknown</b>, never as "discriminates" —
/// assuming a shape can rank is the failure this exists to prevent.
/// </para>
/// </remarks>
internal static class TypedMemEvalRetrieverSensitivity
{
    /// <summary>Per-shape sensitivity for one vertical, or null when the corpus declares none.</summary>
    internal static IReadOnlyDictionary<string, ShapeSensitivity>? For(string verticalSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verticalSlug);

        var assembly = typeof(TypedMemEvalRunner).Assembly;
        var matches = assembly.GetManifestResourceNames().Where(resource =>
                resource.Contains($".{verticalSlug}.", StringComparison.OrdinalIgnoreCase)
                && resource.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1) return null;

        using var stream = assembly.GetManifestResourceStream(matches[0]);
        if (stream is null) return null;

        try
        {
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("probes", out var probes) ||
                !probes.TryGetProperty("retriever_sensitivity", out var sensitivity) ||
                !sensitivity.TryGetProperty("by_shape", out var byShape))
            {
                return null;
            }

            var result = new Dictionary<string, ShapeSensitivity>(StringComparer.Ordinal);
            foreach (var shape in byShape.EnumerateObject())
            {
                var value = shape.Value;
                // A shape missing the flag is UNKNOWN, not discriminating. Defaulting to true would
                // silently place an unrankable shape on the scoreboard.
                if (!value.TryGetProperty("discriminates_under_dense", out var flag) ||
                    flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    continue;
                }

                result[shape.Name] = new ShapeSensitivity(
                    Discriminates: flag.GetBoolean(),
                    HeadroomDense: Read(value, "predicted_headroom_dense"),
                    HeadroomBm25: Read(value, "predicted_headroom_bm25"));
            }

            return result.Count == 0 ? null : result;
        }
        catch (JsonException)
        {
            return null;
        }

        static double? Read(JsonElement element, string name) =>
            element.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : null;
    }

    /// <summary>Prints, per shape, whether it can rank systems for an embedding retriever.</summary>
    internal static void Print(TypedMemEvalVerticalDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (For(descriptor.Slug) is not { } shapes)
        {
            Console.WriteLine(
                "typedmemeval: retriever sensitivity — NOT DECLARED by this corpus (cannot tell "
                + "whether its shapes rank systems under a dense retriever).");
            return;
        }

        foreach (var (shape, sensitivity) in shapes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var verdict = sensitivity.Discriminates
                ? "ranks under dense"
                : "⛔ NON-RANKING FOR US — dense retrieval cannot separate systems on this shape";

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"typedmemeval: sensitivity {shape} — {verdict}"
                + $" (headroom dense {Fmt(sensitivity.HeadroomDense)}, bm25 {Fmt(sensitivity.HeadroomBm25)})"));
        }

        static string Fmt(double? value) =>
            value is { } v ? v.ToString("F3", CultureInfo.InvariantCulture) : "?";
    }
}

/// <summary>One shape's behaviour under a dense retriever, as the corpus predicts it.</summary>
/// <param name="Discriminates">
/// False means the shape cannot rank two systems for an embedding consumer — <b>it is non-ranking
/// for us whatever score it produces.</b>
/// </param>
/// <param name="HeadroomDense">Predicted headroom under a dense retriever — OUR condition.</param>
/// <param name="HeadroomBm25">Predicted headroom under BM25@K=5 — the condition every published figure carries.</param>
internal readonly record struct ShapeSensitivity(
    bool Discriminates, double? HeadroomDense, double? HeadroomBm25);
