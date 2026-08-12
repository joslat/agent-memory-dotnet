using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// A frozen corpus may only be evaluated by a run that would have built it the same way.
/// </summary>
/// <remarks>
/// <para>
/// A cold build is ~684 extraction calls and 7–9 hours, so every real measurement reuses a frozen
/// corpus. Reuse read the sealed manifest, printed its fingerprint and proceeded — and
/// <c>VerifyIntegrity</c> only checks the manifest against <i>itself</i>. Nothing compared it to the
/// run adopting it.
/// </para>
/// <para>
/// The failure that allows is the worst kind: a corpus ingested with <c>AssistantContent=Utterance</c>
/// evaluated by a run configured for <c>Ignore</c> produces a report whose fingerprint describes the
/// run while every fact came from the other configuration. Internally consistent, reproducible, and
/// wrong.
/// </para>
/// </remarks>
public sealed class PreparedCorpusDriftTests
{
    private static LongMemEvalPreparedQuestion Question(int number) => new(
        number, $"q-{number}", "history-sha", "scope-sha", 10, 2, 3,
        new LongMemEvalGraphSnapshot(1, 1, 1, 1, 1, 1, 1, 1, 1));

    private static LongMemEvalPreparationManifest Prepared(
        string assistantContent = "Ignore",
        string extractionModel = "gpt-5.5",
        string datasetSha = "dataset-sha",
        int seed = 42,
        int questions = 2,
        IReadOnlyList<string>? memoryTypes = null) =>
        LongMemEvalPreparationManifest.Create(
            preparationId: "prep-1",
            datasetSha256: datasetSha,
            agentEvalRevision: "0.20.0",
            scopeRunId: "scope",
            answerModelId: "gpt-5.5",
            judgeModelId: "gpt-5.5",
            extractionModelId: extractionModel,
            embeddingModelId: "text-embedding-3-small",
            embeddingDimensions: 1536,
            maxRelevantMessages: 30,
            extractionSourceTime: "metadata-only",
            questions: Enumerable.Range(1, questions).Select(Question).ToArray(),
            initialExtractionCalls: 100,
            useUnifiedExtraction: true,
            useMultiSessionBatchExtraction: true,
            assistantContent: assistantContent,
            usePredicateVocabulary: true,
            extractionVocabularySha256: "vocab-sha",
            queryRelationLexiconSha256: "lexicon-sha",
            preparedAtUtc: "2026-08-10T09:26:14Z",
            description: "the stratified 50",
            memoryTypes: memoryTypes ?? [],
            questionSeed: seed);

    private static PreparedCorpusIdentity Current(
        string assistantContent = "Ignore",
        string extractionModel = "gpt-5.5",
        string datasetSha = "dataset-sha",
        int seed = 42,
        int questions = 2,
        IReadOnlyList<string>? memoryTypes = null) => new()
    {
        DatasetSha256 = datasetSha,
        ExtractionModelId = extractionModel,
        EmbeddingModelId = "text-embedding-3-small",
        EmbeddingDimensions = 1536,
        AssistantContent = assistantContent,
        ExtractionProvenance = "Batch",
        UsePredicateVocabulary = true,
        ExtractionVocabularySha256 = "vocab-sha",
        QueryRelationLexiconSha256 = "lexicon-sha",
        QuestionSeed = seed,
        QuestionCount = questions,
        MemoryTypes = memoryTypes ?? [],
    };

    [Fact]
    public void AMatchingCorpusReportsNoDrift()
    {
        // Reuse must stay the normal case. A check that flags a legitimate reuse trains the operator
        // to pass the override, which is worse than having no check at all.
        LongMemEvalPreparedCorpusDrift.Compare(Prepared(), Current()).Should().BeEmpty();
    }

    [Fact]
    public void AssistantContentDriftIsCaught()
    {
        // THE case. Recorded corpora on this machine were built with Utterance; today's default is
        // Ignore, and nothing would have noticed.
        var drift = LongMemEvalPreparedCorpusDrift.Compare(
            Prepared(assistantContent: "Utterance"), Current(assistantContent: "Ignore"));

        drift.Should().ContainSingle().Which.Field.Should().Be("assistantContent");
    }

    [Theory]
    [InlineData("extractionModel")]
    [InlineData("datasetSha256")]
    [InlineData("questionSeed")]
    [InlineData("questionCount")]
    [InlineData("memoryTypes")]
    public void EveryIngestionAffectingFieldIsCompared(string field)
    {
        var drift = field switch
        {
            "extractionModel" => LongMemEvalPreparedCorpusDrift.Compare(
                Prepared(extractionModel: "gpt-4o"), Current(extractionModel: "gpt-5.5")),
            "datasetSha256" => LongMemEvalPreparedCorpusDrift.Compare(
                Prepared(datasetSha: "old"), Current(datasetSha: "new")),
            "questionSeed" => LongMemEvalPreparedCorpusDrift.Compare(
                Prepared(seed: 42), Current(seed: 7)),
            "questionCount" => LongMemEvalPreparedCorpusDrift.Compare(
                Prepared(questions: 2), Current(questions: 3)),
            "memoryTypes" => LongMemEvalPreparedCorpusDrift.Compare(
                Prepared(memoryTypes: []), Current(memoryTypes: ["episodic"])),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        drift.Select(d => d.Field).Should().Contain(field);
    }

    [Fact]
    public void AnEpisodicSampleCannotSilentlyReuseAStratifiedCorpus()
    {
        // The concrete consequence for 8.3b, asserted rather than assumed: --memory-types episodic
        // selects DIFFERENT questions, whose histories were never ingested into the stratified corpus,
        // so it needs its own cold build. Without this the run would evaluate episodic questions
        // against a graph that has never seen them and report a very low score as a finding.
        var drift = LongMemEvalPreparedCorpusDrift.Compare(
            Prepared(memoryTypes: []), Current(memoryTypes: ["episodic"]));

        drift.Should().NotBeEmpty();
    }

    [Fact]
    public void AnOlderManifestThatRecordedNothingIsDriftNotAgreement()
    {
        // Schema 5 and earlier carried none of the ingestion-identity fields, so deserialising one
        // fills them from the record's DEFAULTS -- "Ignore", "Batch", false. Those are plausible
        // values, and comparing against them reported a corpus built with Utterance as MATCHING a run
        // configured for Ignore. Unknown silently reading as agreement is the one thing this check
        // exists to prevent, and it survived the first version because the two defaults coincided.
        var legacy = Prepared(assistantContent: "Ignore") with { SchemaVersion = 5 };

        var drift = LongMemEvalPreparedCorpusDrift.Compare(legacy, Current(assistantContent: "Ignore"));

        drift.Select(d => d.Field).Should()
            .Contain("assistantContent", "the default must not stand in for a value never recorded")
            .And.Contain("extractionProvenance")
            .And.Contain("usePredicateVocabulary")
            .And.Contain("questionSeed");

        // memoryTypes is the exception, and deliberately so: empty means "every type", and a corpus
        // built before typed sampling existed genuinely IS an all-types corpus. Empty-against-empty
        // is a real match here rather than an unrecorded value, which is why an episodic run against
        // the same legacy corpus still drifts (asserted separately).
        drift.Select(d => d.Field).Should().NotContain("memoryTypes");
    }

    [Fact]
    public void ALegacyCorpusStillDriftsAgainstATypedRun()
    {
        // The other half of the memoryTypes exception: "all types" matching "all types" must not also
        // mean it matches an episodic sample, whose questions it never ingested.
        var legacy = Prepared() with { SchemaVersion = 5 };

        LongMemEvalPreparedCorpusDrift.Compare(legacy, Current(memoryTypes: ["episodic"]))
            .Select(d => d.Field).Should().Contain("memoryTypes");
    }

    [Fact]
    public void ACurrentManifestIsStillComparedOnItsRealValues()
    {
        // The other side of that: schema 6 records these for real, so a matching corpus must still
        // report clean rather than being blanketed as unrecorded.
        LongMemEvalPreparedCorpusDrift.Compare(Prepared(), Current()).Should().BeEmpty();
    }

    [Fact]
    public void EvaluationOnlySettingsAreNotDrift()
    {
        // The reusability that makes a frozen corpus worth keeping. The answer model, judge, recall
        // budget and evidence mode are applied at evaluation time and change no stored fact, so a
        // corpus is legitimately reusable across them -- and flagging them would make every reuse
        // "stale".
        var prepared = Prepared() with { AnswerModelId = "gpt-4o", MaxRelevantMessages = 10 };

        LongMemEvalPreparedCorpusDrift.Compare(prepared, Current()).Should().BeEmpty();
    }

    [Fact]
    public void TheRefusalNamesEveryDriftedFieldAndTheOverride()
    {
        // An operator who cannot find the escape hatch reaches for a cold build instead, which spends
        // nine hours to avoid a warning.
        var drift = LongMemEvalPreparedCorpusDrift.Compare(
            Prepared(assistantContent: "Utterance", seed: 42), Current(assistantContent: "Ignore", seed: 7));

        var message = LongMemEvalPreparedCorpusDrift.Explain("vol-1", "2026-08-10T09:26:14Z", drift);

        message.Should().Contain("vol-1").And.Contain("2026-08-10")
            .And.Contain("assistantContent").And.Contain("questionSeed")
            .And.Contain("--allow-stale-prepared");
    }

    [Fact]
    public void AnUnrecordedPreparationDateStillProducesAReadableRefusal()
    {
        var drift = LongMemEvalPreparedCorpusDrift.Compare(
            Prepared(seed: 42), Current(seed: 7));

        LongMemEvalPreparedCorpusDrift.Explain("vol-1", "", drift).Should().Contain("unknown date");
    }

    [Fact]
    public void TheFingerprintSeparatesTwoCorporaThatDifferOnlyInIngestion()
    {
        // The manifest fingerprint is what a report cites. Before schema 6 these two hashed
        // identically, so two materially different corpora were indistinguishable in every artifact.
        Prepared(assistantContent: "Utterance").Fingerprint
            .Should().NotBe(Prepared(assistantContent: "Ignore").Fingerprint);
    }

    [Fact]
    public void TheDescriptionDoesNotChangeTheFingerprint()
    {
        // Catalog metadata is for humans. Two corpora that differ only in their description are the
        // same corpus, and making a note change the identity would force needless rebuilds.
        var a = Prepared();
        var b = a with { Description = "something else" };

        LongMemEvalPreparationManifest.ComputeFingerprint(b).Should().Be(a.Fingerprint);
    }

    // ── older corpora must stay readable ──────────────────────────────────

    [Fact]
    public void AManifestSealedUnderAnOlderSchemaStillVerifies()
    {
        // THE regression this caught on real data. Bumping CurrentSchemaVersion to 6 made
        // VerifyIntegrity reject every corpus sealed at schema 5 -- each one a 7-9 hour build --
        // which is the precise opposite of what recording ingestion identity is for. Older schemas
        // are read with the field set they were written under; only NEWER ones are refused.
        var current = Prepared();
        var legacy = current with { SchemaVersion = 5 };
        var legacySealed = legacy with
        {
            Fingerprint = LongMemEvalPreparationManifest.ComputeFingerprint(legacy),
        };

        var verify = () => legacySealed.VerifyIntegrity();

        verify.Should().NotThrow();
    }

    [Fact]
    public void ALegacyManifestHashesDifferentlyFromACurrentOne()
    {
        // The two field sets must stay genuinely separate: if the legacy path silently included the
        // schema-6 fields, an old manifest would fail to verify for a reason nobody could see.
        var current = Prepared();
        var legacy = current with { SchemaVersion = 5 };

        LongMemEvalPreparationManifest.ComputeFingerprint(legacy)
            .Should().NotBe(LongMemEvalPreparationManifest.ComputeFingerprint(current));
    }

    [Fact]
    public void AManifestFromANewerBuildIsRefused()
    {
        // It was written by a build that knew things this one does not, so its fingerprint cannot be
        // recomputed here and its extra fields would be dropped in silence.
        var future = Prepared() with { SchemaVersion = 99 };

        var verify = () => future.VerifyIntegrity();

        verify.Should().Throw<InvalidOperationException>().WithMessage("*newer build*");
    }

    [Fact]
    public void ATamperedManifestStillFailsAtEverySchema()
    {
        // Version tolerance must not become "any fingerprint will do". The point of the check is that
        // a manifest matches its own contents.
        var tampered = Prepared() with { Fingerprint = "not-the-real-hash" };

        ((Action)(() => tampered.VerifyIntegrity())).Should().Throw<InvalidOperationException>();
        ((Action)(() => (tampered with { SchemaVersion = 5 }).VerifyIntegrity()))
            .Should().Throw<InvalidOperationException>();
    }
}
