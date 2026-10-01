using AgentMemory.Abstractions.Options;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// Configuration options for LLM-backed extractors.
/// </summary>
public sealed class LlmExtractionOptions
{
    /// <summary>
    /// Sampling temperature for the LLM call (0.0 = deterministic).
    /// </summary>
    public float Temperature { get; set; } = 0.0f;

    /// <summary>
    /// Number of retry attempts on transient failures.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Log the first 2,000 characters of a reply that could not be used (Warning level). Off by default:
    /// the reply is model output about the user's conversation, so it is content, not telemetry.
    /// </summary>
    public bool LogRawResponseOnFailure { get; set; }

    /// <summary>
    /// Whether extraction requests should ask the chat provider for a JSON response.
    /// Disable only for providers that do not support the portable response-format hint.
    /// </summary>
    public bool UseJsonResponseFormat { get; set; } = true;

    /// <summary>
    /// What extraction does with what the <b>assistant</b> said.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="AssistantContentMode.Ignore"/>, which is the behaviour every existing
    /// measurement was taken under and reproduces today's prompts byte-for-byte. The other modes
    /// store different things and therefore retrieve differently; which is better is an empirical
    /// question this setting exists to let us ask.
    /// </remarks>
    public AssistantContentMode AssistantContent { get; set; } = AssistantContentMode.Ignore;

    /// <summary>
    /// Uses one typed model response for entities, facts, preferences, and relationships.
    /// Disabled by default until the unified path passes live extraction-quality acceptance;
    /// the existing four-category extraction path remains the compatibility control.
    /// </summary>
    /// <summary>
    /// Whether extraction records how long a fact is expected to hold (prospective memory).
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="TemporalValidityMode.Ignore"/>, which appends nothing to any prompt and
    /// so reproduces the prompts every existing measurement was taken with, byte for byte.
    /// </remarks>
    /// <summary>
    /// Optional sampling seed sent with every extraction call, for reproducible output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Extraction asks for <c>Temperature = 0</c>, but some deployments reject
    /// an explicit zero outright and the request is dropped, leaving the provider default of 1.0.
    /// Measured consequence: three cold builds of one configuration stored 6,078 / 6,199 / 6,272
    /// canonical triples with only <b>7.5% common to all three</b>, and scored 25 accuracy points
    /// apart. "Same configuration" did not mean "same memory".
    /// </para>
    /// <para>
    /// A seed is the other lever the API offers. <b>It is best-effort</b> — the provider only promises
    /// reproducibility when its <c>system_fingerprint</c> is unchanged too — so whether it helps must
    /// be MEASURED on the deployment in use rather than assumed. Null by default, which sends nothing
    /// and reproduces today's behaviour exactly.
    /// </para>
    /// </remarks>
    public int? Seed { get; set; }

    public TemporalValidityMode TemporalValidity { get; set; } = TemporalValidityMode.Ignore;

    /// <summary>
    /// Records identities the conversation <b>states</b> — "the new flat is the place on Ferrow Row"
    /// — as aliases on the entity, so two surface names for one referent become one entity (E-1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is capture, not inference.</b> <c>ExtractionOptions.LinkFactsToEntities</c>'s remarks say alias
    /// resolution is a separate thing this codebase had not built; measuring the corpus showed that
    /// the hard version of it is not what the questions need. Every alias case in the conjunction
    /// vertical declares itself in a gold session, in as many words: <i>"Head office is the
    /// Calderwick office."</i> Nothing has to be guessed from name similarity — which could never
    /// work anyway, since those two strings are no more alike than any other pair.
    /// </para>
    /// <para>
    /// <b>The field was plumbed end to end and never fed.</b> <c>ExtractedEntity.Aliases</c> flows
    /// into <c>Entity.Aliases</c>, resolution merges them, and <c>GetByNameAsync</c> matches them —
    /// but the multi-session prompt's only example is <c>"aliases":[]</c>, immediately under "use
    /// empty arrays when a category has no supported memory", and nothing anywhere tells the model
    /// what an alias is. The per-kind extractor says "Include aliases when mentioned"; the batch
    /// extractor the benchmark actually runs says nothing. One instruction, two rungs, and the
    /// measured one was the silent rung.
    /// </para>
    /// <para>
    /// Off by default: it changes what ingestion records, and every measurement on record was taken
    /// without it.
    /// </para>
    /// </remarks>
    public bool CaptureIdentityAliases { get; set; }

    /// <summary>
    /// I-5 (default true since 2026-09-27): ask every extractor for the user's own name as a
    /// <c>user | is named | &lt;name&gt;</c> fact when the user states it. It is what
    /// <c>ExtractionOptions.ResolveUserToName</c> reads, one layer down; off, every prompt is byte-for-byte
    /// what it was before the option existed.
    /// </summary>
    public bool CaptureUserName { get; set; } = true;

    /// <summary>
    /// A question states nothing (default true since 2026-09-27): no fact, entity or relationship is made
    /// from what the user asks, only from what a question states outright ("my trip to Seville next week").
    /// Measured live: "What do you remember about my brother?" produced an entity "user's brother" and the
    /// fact "Marta has a brother brother". Off, every prompt is byte-for-byte what it was.
    /// </summary>
    public bool IgnoreQuestions { get; set; } = true;

    /// <summary>
    /// 36.3. A preference is the user's own stated taste: someone else's taste is recorded as a fact about
    /// them, and a request is not a preference. Measured live: a taught book produced "user preferences"
    /// that were its characters' ("Alice does not like raw eggs", "The Mouse hates cats"), and "Recommend
    /// some music…" was stored as the user's preference. A preference always renders as the user's, so
    /// both read to the agent as things the person said about themselves. Off, every prompt is
    /// byte-for-byte what it was.
    /// </summary>
    public bool OwnPreferencesOnly { get; set; }

    /// <summary>
    /// 36.4. Ask every extractor to mark what a correction replaces ("actually Arcade Fire, not Radiohead" adds
    /// <c>"replaces": "Radiohead"</c>), so the write can close the old fact or preference even when nothing else
    /// ties the two together (a preference has no single-valued relation; "the full marathon instead of the half"
    /// is a new plan, not a new value of one). Read by <c>ExtractionOptions.SupersedeReplacedFacts</c>. Found in
    /// simulated conversations: both bands, both marathons and both ages stayed live. Off, every prompt is
    /// byte-for-byte what it was.
    /// </summary>
    public bool MarkCorrections { get; set; }

    /// <summary>
    /// J-14. Keep who an event was shared with as facts of their own (default false). "Yesterday I went hiking in Sintra
    /// with my friend Pedro and we got lost" was sometimes kept whole ("went hiking in Sintra with Pedro"), sometimes
    /// split into "went hiking in | Sintra" and "got lost … | Sintra" with Pedro gone, so "who was with me?" had no
    /// answer. With this on, the extractor also writes, for each companion, "user | went hiking with | Pedro" (the
    /// activity with "with", the event's day kept), however it splits the event itself. Off, every prompt is
    /// byte-for-byte what it was.
    /// </summary>
    public bool CaptureEventCompanions { get; set; }

    /// <summary>
    /// How precisely a stored fact or preference is bound to the turn that stated it.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="ExtractionProvenanceMode.Batch"/>, which appends nothing to any prompt
    /// and leaves the transcript unnumbered, so every prompt is byte-for-byte what existing
    /// measurements were taken with. <see cref="ExtractionProvenanceMode.PerItem"/> changes the
    /// rendered conversation as well as the instruction, so it is a stated per-run decision rather
    /// than something a package upgrade can turn on.
    /// </remarks>
    public ExtractionProvenanceMode Provenance { get; set; } = ExtractionProvenanceMode.Batch;

    public bool UseUnifiedExtraction { get; set; }

    /// <summary>
    /// Enables token-bounded multi-session unified extraction through
    /// <c>IMemoryExtractionPipeline.ExtractBatchAsync</c>. Disabled by default; single-session
    /// extraction behavior is unchanged.
    /// </summary>
    public bool UseMultiSessionBatchExtraction { get; set; }

    /// <summary>
    /// Maximum number of planned multi-session batches that one extraction operation may send
    /// concurrently. The default of one preserves the historical sequential provider-call order.
    /// </summary>
    public int MaxConcurrentBatchesPerExtraction { get; set; } = 1;

    /// <summary>
    /// Optional process-local cap shared by all multi-session extraction operations registered in
    /// the same service provider. Zero disables the shared cap.
    /// </summary>
    public int MaxConcurrentExtractionBatches { get; set; }

    /// <summary>
    /// Model identifier to use. <c>null</c> (the default) means use the <c>IChatClient</c> default.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// POLE+O entity types recognised by the entity extractor.
    /// </summary>
    public IReadOnlyList<string> EntityTypes { get; set; } =
        new[] { "PERSON", "ORGANIZATION", "LOCATION", "EVENT", "OBJECT" };

    /// <summary>
    /// Optional override for the entity extractor system prompt.
    /// When null the extractor's built-in default prompt is used.
    /// </summary>
    public string? EntityExtractionPrompt { get; set; }

    /// <summary>
    /// Optional override for the fact extractor system prompt.
    /// When null the extractor's built-in default prompt is used.
    /// </summary>
    public string? FactExtractionPrompt { get; set; }

    /// <summary>
    /// Optional override for the relationship extractor system prompt.
    /// When null the extractor's built-in default prompt is used.
    /// </summary>
    public string? RelationshipExtractionPrompt { get; set; }

    /// <summary>
    /// Optional override for the preference extractor system prompt.
    /// When null the extractor's built-in default prompt is used.
    /// </summary>
    public string? PreferenceExtractionPrompt { get; set; }

    /// <summary>
    /// Offers the established relation vocabulary to the extractor so it reuses relation names
    /// instead of inventing a phrasing per sentence. Default off.
    /// </summary>
    /// <remarks>
    /// QUALITY-RISK: it changes what the model emits, and it lengthens the prompt, which moves the
    /// frozen batch plan's estimated input totals. Opt-in so the effect can be measured against an
    /// unchanged control before it becomes the default.
    /// </remarks>
    public bool UsePredicateVocabulary { get; set; }

    /// <summary>
    /// The extraction half of the Conversational preset (see <c>MemoryOptions.CreateConversational()</c>): dates and
    /// periods from the conversation's own time, corrections marked, only the speaker's own preferences, and who an
    /// event was shared with, through the unified extractor. Returns this instance, so it composes in a configure lambda.
    /// </summary>
    /// <remarks>
    /// These change what the model is asked, so they ship in the preset rather than as defaults; the preset was checked
    /// against the defaults on scripted conversations with answer and graph checks (PLAN 40.9).
    /// </remarks>
    public LlmExtractionOptions ApplyConversational()
    {
        TemporalValidity = TemporalValidityMode.Extract;
        MarkCorrections = true;
        OwnPreferencesOnly = true;
        CaptureEventCompanions = true;
        UseUnifiedExtraction = true;
        return this;
    }
}
