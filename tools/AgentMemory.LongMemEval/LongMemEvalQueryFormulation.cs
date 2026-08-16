using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>How the retrieval query is derived from the question (27.4).</summary>
internal enum LongMemEvalQueryFormulation
{
    /// <summary>The question text, verbatim. What ships, and the control.</summary>
    Verbatim = 0,

    /// <summary>One model call restating the question as a standalone search query.</summary>
    Rewrite = 1,

    /// <summary>The question plus generated near-synonyms and entity aliases.</summary>
    Expansion = 2,
}

/// <summary>
/// Derives the retrieval query from the question, and records whether it actually changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The last untested retrieval lever.</b> The query has always been the question text, used
/// verbatim — no rewriting, no expansion, no hypothetical-answer generation. Decision rules and the
/// expected ceiling are pre-registered in
/// <c>docs/reviews/query-formulation-preregistration.md</c>; read that before reading any number this
/// produces.
/// </para>
/// <para>
/// <b>The rewriter must not be allowed to fail quietly.</b> If it returns the input unchanged, or
/// throws and falls back, the arm measured the control while claiming to measure a treatment — the
/// exact shape that voided six procedural-benefit runs. So every derivation reports whether the query
/// differed, and the run voids when too few did.
/// </para>
/// </remarks>
internal sealed class LongMemEvalQueryFormulator(
    IChatClient chatClient,
    LongMemEvalQueryFormulation mode)
{
    private const string RewritePrompt =
        "Rewrite the user's question as a standalone search query for a memory store of past "
        + "conversations. Keep every proper noun, date and number. Drop conversational framing. "
        + "Reply with the query only, no preamble.";

    private const string ExpansionPrompt =
        "Expand the user's question into a search query for a memory store of past conversations. "
        + "Keep the original wording, then append near-synonyms and likely alternative phrasings for "
        + "its key terms, separated by spaces. Keep every proper noun, date and number. "
        + "Reply with the query only, no preamble.";

    private int _derived;
    private int _changed;
    private int _failed;

    /// <summary>Questions whose derived query differed from the original.</summary>
    public int Changed => _changed;

    /// <summary>Questions where the model call threw and the original was used.</summary>
    public int Failed => _failed;

    /// <summary>Questions processed.</summary>
    public int Derived => _derived;

    public LongMemEvalQueryFormulation Mode => mode;

    /// <summary>
    /// The query to retrieve with. Returns the question unchanged on the control arm, and on any
    /// failure — a failure is counted, never hidden.
    /// </summary>
    public async Task<string> DeriveAsync(string question, CancellationToken cancellationToken = default)
    {
        if (mode == LongMemEvalQueryFormulation.Verbatim) return question;

        Interlocked.Increment(ref _derived);
        try
        {
            var response = await chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System,
                        mode == LongMemEvalQueryFormulation.Rewrite ? RewritePrompt : ExpansionPrompt),
                    new ChatMessage(ChatRole.User, question),
                ],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var derived = (response.Text ?? string.Empty).Trim();

            // An empty rewrite is a failure, not a query. Retrieving on "" would return the corpus in
            // arbitrary order and score as a catastrophic retrieval regression caused by the harness.
            if (derived.Length == 0)
            {
                Interlocked.Increment(ref _failed);
                return question;
            }

            if (!string.Equals(derived, question, StringComparison.Ordinal))
                Interlocked.Increment(ref _changed);

            return derived;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Counted and surfaced. A silent fallback to the original question is precisely how an arm
            // comes to measure its own control.
            Interlocked.Increment(ref _failed);
            return question;
        }
    }

    /// <summary>
    /// Null when the arm is sound; otherwise the reason it must be reported VOID.
    /// </summary>
    /// <param name="questionsAnswered">
    /// How many questions the arm actually answered, so "it barely ran" is distinguishable from
    /// "it ran and changed everything".
    /// </param>
    /// <remarks>
    /// <para>
    /// Two independent ways the treatment fails to be a treatment, and the first version of this
    /// witness only caught one. It compared <i>changed / derived</i>, which is 100% when the
    /// formulator ran on two questions and rewrote both — and the first real run did exactly that,
    /// reporting <c>voidReason: null</c> on an arm where 48 of 50 questions never reached retrieval at
    /// all. A witness that can be satisfied by a sample of two is not a witness.
    /// </para>
    /// <para>
    /// The 80% floors are pre-registered. Below either one, the mechanism was not applied to enough
    /// questions for a coverage delta to mean anything.
    /// </para>
    /// </remarks>
    public string? VoidReason(int questionsAnswered)
    {
        if (mode == LongMemEvalQueryFormulation.Verbatim) return null;

        // Coverage: did it run at all, on the questions the arm actually answered?
        if (questionsAnswered > 0 && (double)_derived / questionsAnswered < 0.80)
        {
            return $"query formulation ran on only {_derived} of {questionsAnswered} answered "
                + $"questions ({(double)_derived / questionsAnswered:P0}, floor 80%). The arm did not "
                + "apply the treatment to enough questions to compare against anything.";
        }

        if (_derived == 0) return null;

        // Effect: of the ones it ran on, did it actually change the query?
        return (double)_changed / _derived >= 0.80
            ? null
            : $"query formulation changed only {_changed} of {_derived} queries "
              + $"({(double)_changed / _derived:P0}, floor 80%), with {_failed} failure(s). "
              + "The arm largely measured its own control.";
    }
}
