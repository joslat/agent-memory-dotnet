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

                // NOT-APPLICABLE shapes publish NO allgold, headroom or boolean -- deliberately, so
                // the row cannot be averaged by accident. `forgetting/never-known` is one: every
                // question has an EMPTY gold set, so `gold.issubset(top_k)` is vacuously true and
                // ALLgold would read 1.000 under every retriever at every budget. The most
                // flattering number available and the least true. Carried by its verdict alone.
                if (ReadAgreement(value) == DenseRankingClass.NotApplicable)
                {
                    result[shape.Name] = new ShapeSensitivity(
                        Discriminates: false, HeadroomDense: null, HeadroomBm25: null,
                        Agreement: DenseRankingClass.NotApplicable, SecondDenseHeadroom: null);
                    continue;
                }

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
                    HeadroomBm25: Read(value, "predicted_headroom_bm25"),
                    Agreement: ReadAgreement(value),
                    SecondDenseHeadroom: value.TryGetProperty("second_dense", out var second)
                        ? Read(second, "predicted_headroom")
                        : null);
            }

            return result.Count == 0 ? null : result;
        }
        catch (JsonException)
        {
            return null;
        }

        static double? Read(JsonElement element, string name) =>
            element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var d) ? d : null;

        // THE PUBLISHED VERDICT, never recomputed. 0.38 co-publishes `retriever_agreement`
        // precisely because two derivations of one number drift and the drift is invisible to both
        // sides -- which is exactly what happened here: a locally derived version of this field
        // classified SIX shapes as robust that the publisher calls retriever-sensitive, including
        // `prospective/due-window`. An absent or unrecognised verdict reads as UNKNOWN, never as
        // robust.
        static DenseRankingClass ReadAgreement(JsonElement element) =>
            element.TryGetProperty("retriever_agreement", out var verdict)
            && verdict.ValueKind == JsonValueKind.String
                ? verdict.GetString() switch
                {
                    "robust-ranking" => DenseRankingClass.Robust,
                    "retriever-sensitive" => DenseRankingClass.RetrieverSensitive,
                    "non-ranking" => DenseRankingClass.NonRanking,
                    "not-applicable" => DenseRankingClass.NotApplicable,
                    _ => DenseRankingClass.Unknown,
                }
                : DenseRankingClass.Unknown;
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
            // THE PUBLISHED VERDICT LEADS. `discriminates_under_dense` answers a narrower question --
            // can this shape separate two systems under a dense retriever at all -- and a shape can
            // clear it while still moving a long way between retrievers. Printing only that flag is
            // how a retriever-sensitive shape reads as safely rankable, which is the exact mistake
            // that put six shapes in the wrong class here before `retriever_agreement` was published.
            var verdict = sensitivity.Agreement switch
            {
                DenseRankingClass.Robust =>
                    "ROBUST-RANKING — discriminates under both published retrievers",
                DenseRankingClass.RetrieverSensitive =>
                    "⚠ RETRIEVER-SENSITIVE — the verdict moves with the embedder; never load-bearing alone",
                DenseRankingClass.NonRanking =>
                    "⛔ NON-RANKING FOR US — dense retrieval cannot separate systems on this shape",
                DenseRankingClass.NotApplicable =>
                    "— NOT APPLICABLE — retrieval is undefined here (empty gold set), not merely unmeasured",
                // Never reads as robust. An unreadable verdict is a reason to withhold, not to assume.
                _ => "⛔ UNKNOWN — no published verdict could be read; NOT treated as robust",
            };

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"typedmemeval: sensitivity {shape} — {verdict}"
                + $" (dense {Describe(sensitivity.Discriminates)}, headroom dense {Fmt(sensitivity.HeadroomDense)}"
                + $", second dense {Fmt(sensitivity.SecondDenseHeadroom)}, bm25 {Fmt(sensitivity.HeadroomBm25)})"));
        }

        static string Fmt(double? value) =>
            value is { } v ? v.ToString("F3", CultureInfo.InvariantCulture) : "?";

        static string Describe(bool discriminates) => discriminates ? "discriminates" : "does not discriminate";
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
    bool Discriminates,
    double? HeadroomDense,
    double? HeadroomBm25,
    DenseRankingClass Agreement,
    double? SecondDenseHeadroom);

/// <summary>
/// How much confidence a shape's ranking power deserves — <b>read from the package, never derived.</b>
/// </summary>
/// <remarks>
/// <para>
/// AgentEval co-publish <c>retriever_agreement</c> from 0.38 because a derived copy drifts. This
/// project proved that on day one: a locally computed version classified <b>six</b> shapes as robust
/// that the publisher calls retriever-sensitive — <c>arithmetic/delta</c>, <c>episodic/list-order</c>,
/// <c>prospective/due-window</c>, <c>temporal/recency</c>, <c>workingmemory/distance-8</c> and
/// <c>distance-60</c>. The local rule tested only whether both retrievers cleared the discrimination
/// threshold; it did not test whether the headroom MOVED between them, and on those six it moves a
/// long way (recency 0.467 → 0.200).
/// </para>
/// </remarks>
internal enum DenseRankingClass
{
    /// <summary>No verdict could be read. Never treated as robust.</summary>
    Unknown,

    /// <summary>Discriminates under BOTH published retrievers — ranks us with confidence.</summary>
    Robust,

    /// <summary>
    /// Flips or moves materially between the two. Read cautiously and <b>never load-bearing for a
    /// ship/no-ship decision alone</b> — the value here is a fact about the embedder, not the engine.
    /// </summary>
    RetrieverSensitive,

    /// <summary>Fails under both — non-ranking for us.</summary>
    NonRanking,

    /// <summary>
    /// Retrieval is UNDEFINED for this shape, not unmeasured: its gold set is empty, so
    /// <c>gold.issubset(top_k)</c> is vacuously true and ALLgold would read 1.000 under every
    /// retriever at every budget. Published with no allgold or headroom fields at all, deliberately,
    /// so the row cannot be averaged by accident.
    /// </summary>
    NotApplicable,
}
