using AgentMemory.Abstractions.Options;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// The unified extractor must honour <see cref="LlmExtractionOptions.EntityTypes"/> before its flag can
/// become the shipped default.
/// </summary>
/// <remarks>
/// Until now it read only <c>UseUnifiedExtraction</c> and <c>AssistantContent</c>, with its type list
/// hardcoded as a const. Flipping the default in that state would have made a consumer's configured
/// <c>EntityTypes</c> silently stop taking effect — no error, no warning, no compile break. A silent
/// behaviour change is the one outcome worth blocking a default flip over.
/// </remarks>
public sealed class UnifiedExtractionOptionParityTests
{
    /// <summary>
    /// The exact type fragment shipped today. Reproduced as a literal rather than referenced, so that a
    /// change to the production string cannot quietly update the expectation with it.
    /// </summary>
    private const string ShippedTypeFragment = "PERSON|ORGANIZATION|LOCATION|EVENT|OBJECT";

    /// <summary>
    /// The complete prompt as it shipped, transcribed from the pre-refactor <c>SystemPrompt</c> const at
    /// commit <c>fdfe2fb</c>. 599 characters.
    /// </summary>
    /// <remarks>
    /// Pinned as an independent literal ON PURPOSE. The first version of this test compared
    /// <c>BuildSystemPrompt(mode, defaults)</c> against <c>BuildSystemPrompt(mode)</c> — a tautology,
    /// since the one-argument overload delegates to the other. It passed while proving nothing, and
    /// would have passed just as happily had the refactor corrupted the prompt.
    /// </remarks>
    private const string ShippedPromptAtDefaults =
        """
        You extract structured long-term memory from a conversation.
        Return JSON only with all four arrays: entities, facts, preferences, relations.
        Use exactly this shape:
        {"entities":[{"name":"...","type":"PERSON|ORGANIZATION|LOCATION|EVENT|OBJECT","confidence":0.9,"aliases":[]}],"facts":[{"subject":"...","predicate":"...","object":"...","confidence":0.9}],"preferences":[{"category":"...","preference":"...","confidence":0.85}],"relations":[{"source":"...","target":"...","relation_type":"...","confidence":0.8}]}
        Use empty arrays when a category has no supported memory. Do not emit prose or markdown.
        """;

    /// <summary>
    /// Compares prompt CONTENT, not the line endings of the file a literal happens to live in.
    /// </summary>
    /// <remarks>
    /// The production const sits in a CRLF file and this test's literal in an LF one, so a raw
    /// comparison fails at index 60 — the first line break — while the text is identical. That
    /// difference is a property of the source files, not of the prompt, and either file's endings
    /// could be flipped by an editor or by git normalization at any time.
    /// </remarks>
    private static string Content(string value) => value.Replace("\r\n", "\n");

    [Fact]
    public void DefaultEntityTypesReproduceTheShippedPromptCharacterForCharacter()
    {
        // THE STRICT ONE. Every sealed base and every quality number in this track was produced by the
        // prompt text above. Making the type list dynamic must be a pure refactor at default settings:
        // one character of drift changes what the model extracts, which changes what is stored, which
        // invalidates every measurement taken so far. AssistantContentMode.Ignore appends nothing, so
        // the built prompt must equal the pinned literal.
        var options = new LlmExtractionOptions();

        var prompt = LlmUnifiedMemoryExtractor.BuildSystemPrompt(
            AssistantContentMode.Ignore, options.EntityTypes);

        Content(prompt).Should().Be(Content(ShippedPromptAtDefaults));

        // Length is asserted separately as a second, independent witness: normalising line endings
        // could in principle mask a stray \r, and 599 is the measured length of the shipped prompt.
        Content(prompt).Length.Should().Be(599);
    }

    [Fact]
    public void ConfiguredEntityTypesReachTheUnifiedPrompt()
    {
        // The defect this whole test class exists for: custom types were dropped on the floor.
        var prompt = LlmUnifiedMemoryExtractor.BuildSystemPrompt(
            AssistantContentMode.Ignore, new[] { "PRODUCT", "SKU" });

        prompt.Should().Contain("PRODUCT|SKU");
        prompt.Should().NotContain(ShippedTypeFragment);
    }

    [Fact]
    public void AnEmptyEntityTypeListFallsBackToTheBuiltInTypes()
    {
        // An empty list is a misconfiguration, not a request for an entity-less prompt. Emitting
        // `"type":""` would ask the model for a type it can never satisfy. Matches the per-kind
        // extractor, which falls back to DefaultEntityTypes on an empty list.
        LlmUnifiedMemoryExtractor.BuildSystemPrompt(AssistantContentMode.Ignore, [])
            .Should().Contain(ShippedTypeFragment);
    }

    [Fact]
    public void TheAssistantContentInstructionStillAppendsAfterTheTypeList()
    {
        // Guards the composition order: the episodic instruction is appended to the system prompt, and
        // making the types dynamic must not reorder or drop it. Episodic capture is measured behaviour.
        var prompt = LlmUnifiedMemoryExtractor.BuildSystemPrompt(
            AssistantContentMode.Utterance, new[] { "PRODUCT" });

        prompt.Should().Contain("PRODUCT");
        prompt.Should().Contain("assistant");
        prompt.IndexOf("PRODUCT", System.StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("recommended", System.StringComparison.Ordinal));
    }
}
