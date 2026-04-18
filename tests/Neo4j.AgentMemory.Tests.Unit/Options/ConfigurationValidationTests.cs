using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.AgentFramework;
using Neo4j.AgentMemory.Enrichment;
using Neo4j.AgentMemory.Extraction.Llm;
using Neo4j.AgentMemory.McpServer;
using Neo4j.AgentMemory.Neo4j.Infrastructure;

namespace Neo4j.AgentMemory.Tests.Unit.OptionsTests;

/// <summary>
/// Comprehensive defaults and boundary-condition tests for every Options class.
/// </summary>
public sealed class ConfigurationValidationTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Neo4j.AgentMemory.Abstractions
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void MemoryOptions_DefaultConstructor_SubOptionsAreNotNull()
    {
        var o = new MemoryOptions();
        o.ShortTerm.Should().NotBeNull();
        o.LongTerm.Should().NotBeNull();
        o.Reasoning.Should().NotBeNull();
        o.Recall.Should().NotBeNull();
        o.ContextBudget.Should().NotBeNull();
        o.Extraction.Should().NotBeNull();
    }

    [Fact]
    public void MemoryOptions_Default_EnableAutoExtractionIsTrue()
    {
        new MemoryOptions().EnableAutoExtraction.Should().BeTrue();
    }

    [Fact]
    public void MemoryOptions_Default_EnableGraphRagIsFalse()
    {
        new MemoryOptions().EnableGraphRag.Should().BeFalse();
    }

    [Fact]
    public void RecallOptions_StaticDefault_MatchesNewInstance()
    {
        var inst = new RecallOptions();
        RecallOptions.Default.MaxRecentMessages.Should().Be(inst.MaxRecentMessages);
        RecallOptions.Default.MaxRelevantMessages.Should().Be(inst.MaxRelevantMessages);
        RecallOptions.Default.MaxEntities.Should().Be(inst.MaxEntities);
        RecallOptions.Default.MaxPreferences.Should().Be(inst.MaxPreferences);
        RecallOptions.Default.MaxFacts.Should().Be(inst.MaxFacts);
        RecallOptions.Default.MaxTraces.Should().Be(inst.MaxTraces);
        RecallOptions.Default.MaxGraphRagItems.Should().Be(inst.MaxGraphRagItems);
        RecallOptions.Default.MinSimilarityScore.Should().Be(inst.MinSimilarityScore);
        RecallOptions.Default.BlendMode.Should().Be(inst.BlendMode);
    }

    [Fact]
    public void RecallOptions_AllNumericDefaults_ArePositive()
    {
        var o = new RecallOptions();
        o.MaxRecentMessages.Should().BePositive();
        o.MaxRelevantMessages.Should().BePositive();
        o.MaxEntities.Should().BePositive();
        o.MaxPreferences.Should().BePositive();
        o.MaxFacts.Should().BePositive();
        o.MaxTraces.Should().BePositive();
        o.MaxGraphRagItems.Should().BePositive();
    }

    [Fact]
    public void RecallOptions_MinSimilarityScore_IsInValidRange()
    {
        var o = new RecallOptions();
        o.MinSimilarityScore.Should().BeGreaterThanOrEqualTo(0.0).And.BeLessThanOrEqualTo(1.0);
    }

    [Fact]
    public void RecallOptions_Default_BlendModeIsBlended()
    {
        new RecallOptions().BlendMode.Should().Be(RetrievalBlendMode.Blended);
    }

    [Fact]
    public void ShortTermMemoryOptions_Default_GenerateEmbeddingsIsTrue()
    {
        new ShortTermMemoryOptions().GenerateEmbeddings.Should().BeTrue();
    }

    [Fact]
    public void ShortTermMemoryOptions_Default_DefaultRecentMessageLimitIs10()
    {
        new ShortTermMemoryOptions().DefaultRecentMessageLimit.Should().Be(10);
    }

    [Fact]
    public void ShortTermMemoryOptions_Default_MaxMessagesPerQueryIsPositive()
    {
        new ShortTermMemoryOptions().MaxMessagesPerQuery.Should().BePositive();
    }

    [Fact]
    public void ShortTermMemoryOptions_Default_SessionStrategyIsPerConversation()
    {
        new ShortTermMemoryOptions().SessionStrategy.Should().Be(SessionStrategy.PerConversation);
    }

    [Fact]
    public void LongTermMemoryOptions_Default_AllEmbeddingFlagsAreTrue()
    {
        var o = new LongTermMemoryOptions();
        o.GenerateEntityEmbeddings.Should().BeTrue();
        o.GenerateFactEmbeddings.Should().BeTrue();
        o.GeneratePreferenceEmbeddings.Should().BeTrue();
    }

    [Fact]
    public void LongTermMemoryOptions_Default_MinConfidenceThresholdIsInValidRange()
    {
        var o = new LongTermMemoryOptions();
        o.MinConfidenceThreshold.Should().BeGreaterThanOrEqualTo(0.0).And.BeLessThanOrEqualTo(1.0);
    }

    [Fact]
    public void LongTermMemoryOptions_Default_EnableEntityResolutionIsTrue()
    {
        new LongTermMemoryOptions().EnableEntityResolution.Should().BeTrue();
    }

    [Fact]
    public void ExtractionOptions_Default_SubOptionsAreNotNull()
    {
        var o = new ExtractionOptions();
        o.EntityResolution.Should().NotBeNull();
        o.Validation.Should().NotBeNull();
    }

    [Fact]
    public void ExtractionOptions_Default_MinConfidenceThresholdIsNonNegative()
    {
        new ExtractionOptions().MinConfidenceThreshold.Should().BeGreaterThanOrEqualTo(0.0);
    }

    [Fact]
    public void ExtractionOptions_Default_AutoMergeIsEnabled()
    {
        new ExtractionOptions().EnableAutoMerge.Should().BeTrue();
    }

    [Fact]
    public void ExtractionOptions_Default_ConfidenceThresholdsInValidRange()
    {
        var o = new ExtractionOptions();
        o.StrongPatternConfidence.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(1.0);
        o.RegexMatchConfidence.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(1.0);
        o.AutoMergeThreshold.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(1.0);
        o.SameAsThreshold.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(1.0);
    }

    [Fact]
    public void EntityResolutionOptions_Default_AllMatchTypesEnabled()
    {
        var o = new EntityResolutionOptions();
        o.EnableExactMatch.Should().BeTrue();
        o.EnableFuzzyMatch.Should().BeTrue();
        o.EnableSemanticMatch.Should().BeTrue();
    }

    [Fact]
    public void EntityResolutionOptions_Default_TypeStrictFilteringIsTrue()
    {
        new EntityResolutionOptions().TypeStrictFiltering.Should().BeTrue();
    }

    [Fact]
    public void EntityResolutionOptions_Default_ThresholdsInValidRange()
    {
        var o = new EntityResolutionOptions();
        o.FuzzyMatchThreshold.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(1.0);
        o.SemanticMatchThreshold.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(1.0);
    }

    [Fact]
    public void EntityValidationOptions_Default_MinNameLengthIsPositive()
    {
        new EntityValidationOptions().MinNameLength.Should().BePositive();
    }

    [Fact]
    public void EntityValidationOptions_Default_RejectFiltersAreEnabled()
    {
        var o = new EntityValidationOptions();
        o.RejectNumericOnly.Should().BeTrue();
        o.RejectPunctuationOnly.Should().BeTrue();
        o.UseStopwordFilter.Should().BeTrue();
    }

    [Fact]
    public void StreamingExtractionOptions_Default_ChunkSizeIsDefaultValue()
    {
        new StreamingExtractionOptions().ChunkSize.Should().Be(StreamingExtractionOptions.DefaultChunkSize);
    }

    [Fact]
    public void StreamingExtractionOptions_Default_OverlapIsDefaultValue()
    {
        new StreamingExtractionOptions().Overlap.Should().Be(StreamingExtractionOptions.DefaultOverlap);
    }

    [Fact]
    public void StreamingExtractionOptions_Default_ChunkByTokensIsFalse()
    {
        new StreamingExtractionOptions().ChunkByTokens.Should().BeFalse();
    }

    [Fact]
    public void StreamingExtractionOptions_Default_SplitOnSentencesIsTrue()
    {
        new StreamingExtractionOptions().SplitOnSentences.Should().BeTrue();
    }

    [Fact]
    public void StreamingExtractionOptions_Default_ChunkSizeIsPositive()
    {
        new StreamingExtractionOptions().ChunkSize.Should().BePositive();
    }

    [Fact]
    public void StreamingExtractionOptions_Default_OverlapIsPositive()
    {
        new StreamingExtractionOptions().Overlap.Should().BePositive();
    }

    [Fact]
    public void StreamingExtractionOptions_Default_OverlapSmallerThanChunkSize()
    {
        var o = new StreamingExtractionOptions();
        o.Overlap.Should().BeLessThan(o.ChunkSize);
    }

    [Fact]
    public void StreamingExtractionOptions_ForTokens_SetsTokenDefaults()
    {
        var o = StreamingExtractionOptions.ForTokens();
        o.ChunkByTokens.Should().BeTrue();
        o.ChunkSize.Should().Be(StreamingExtractionOptions.DefaultTokenChunkSize);
        o.Overlap.Should().Be(StreamingExtractionOptions.DefaultTokenOverlap);
    }

    [Fact]
    public void ContextCompressionOptions_Default_TokenThresholdIsPositive()
    {
        new ContextCompressionOptions().TokenThreshold.Should().BePositive();
    }

    [Fact]
    public void ContextCompressionOptions_Default_RecentMessageCountIsPositive()
    {
        new ContextCompressionOptions().RecentMessageCount.Should().BePositive();
    }

    [Fact]
    public void ContextCompressionOptions_Default_MaxObservationsIsPositive()
    {
        new ContextCompressionOptions().MaxObservations.Should().BePositive();
    }

    [Fact]
    public void ContextCompressionOptions_Default_EnableReflectionsIsTrue()
    {
        new ContextCompressionOptions().EnableReflections.Should().BeTrue();
    }

    [Fact]
    public void ReasoningMemoryOptions_Default_GenerateTaskEmbeddingsIsTrue()
    {
        new ReasoningMemoryOptions().GenerateTaskEmbeddings.Should().BeTrue();
    }

    [Fact]
    public void ReasoningMemoryOptions_Default_StoreToolCallsIsTrue()
    {
        new ReasoningMemoryOptions().StoreToolCalls.Should().BeTrue();
    }

    [Fact]
    public void ReasoningMemoryOptions_Default_MaxTracesPerSessionIsNull()
    {
        new ReasoningMemoryOptions().MaxTracesPerSession.Should().BeNull();
    }

    [Fact]
    public void ContextBudget_Default_MaxTokensIsNull()
    {
        ContextBudget.Default.MaxTokens.Should().BeNull();
    }

    [Fact]
    public void ContextBudget_Default_MaxCharactersIsNull()
    {
        ContextBudget.Default.MaxCharacters.Should().BeNull();
    }

    [Fact]
    public void ContextBudget_Default_TruncationStrategyIsOldestFirst()
    {
        ContextBudget.Default.TruncationStrategy.Should().Be(TruncationStrategy.OldestFirst);
    }

    [Fact]
    public void ContextBudget_StaticDefault_MatchesNewInstance()
    {
        var inst = new ContextBudget();
        ContextBudget.Default.MaxTokens.Should().Be(inst.MaxTokens);
        ContextBudget.Default.MaxCharacters.Should().Be(inst.MaxCharacters);
        ContextBudget.Default.TruncationStrategy.Should().Be(inst.TruncationStrategy);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Neo4j.AgentMemory.Extraction.Llm
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void LlmExtractionOptions_Default_TemperatureIsZero()
    {
        new LlmExtractionOptions().Temperature.Should().Be(0.0f);
    }

    [Fact]
    public void LlmExtractionOptions_Default_MaxRetriesIs2()
    {
        new LlmExtractionOptions().MaxRetries.Should().Be(2);
    }

    [Fact]
    public void LlmExtractionOptions_Default_ModelIdIsEmpty()
    {
        new LlmExtractionOptions().ModelId.Should().BeEmpty();
    }

    [Fact]
    public void LlmExtractionOptions_Default_EntityTypesContainsCoreTypes()
    {
        var types = new LlmExtractionOptions().EntityTypes;
        types.Should().Contain("PERSON")
            .And.Contain("ORGANIZATION")
            .And.Contain("LOCATION")
            .And.Contain("EVENT")
            .And.Contain("OBJECT");
    }

    [Fact]
    public void LlmExtractionOptions_Default_PromptPropertiesAreNull()
    {
        var o = new LlmExtractionOptions();
        o.EntityExtractionPrompt.Should().BeNull();
        o.FactExtractionPrompt.Should().BeNull();
        o.RelationshipExtractionPrompt.Should().BeNull();
        o.PreferenceExtractionPrompt.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Neo4j.AgentMemory.Neo4j
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Neo4jOptions_Default_UriIsLocalhost()
    {
        new Neo4jOptions().Uri.Should().Be("bolt://localhost:7687");
    }

    [Fact]
    public void Neo4jOptions_Default_UsernameIsNeo4j()
    {
        new Neo4jOptions().Username.Should().Be("neo4j");
    }

    [Fact]
    public void Neo4jOptions_Default_DatabaseIsNeo4j()
    {
        new Neo4jOptions().Database.Should().Be("neo4j");
    }

    [Fact]
    public void Neo4jOptions_Default_MaxConnectionPoolSizeIsPositive()
    {
        new Neo4jOptions().MaxConnectionPoolSize.Should().BePositive();
    }

    [Fact]
    public void Neo4jOptions_Default_ConnectionAcquisitionTimeoutIsPositive()
    {
        new Neo4jOptions().ConnectionAcquisitionTimeout.Should().BePositive();
    }

    [Fact]
    public void Neo4jOptions_Default_EncryptionEnabledIsFalse()
    {
        new Neo4jOptions().EncryptionEnabled.Should().BeFalse();
    }

    [Fact]
    public void Neo4jOptions_Default_EmbeddingDimensionsIs1536()
    {
        new Neo4jOptions().EmbeddingDimensions.Should().Be(1536);
    }

    [Fact]
    public void GraphRagOptions_Default_TopKIsPositive()
    {
        var o = new GraphRagOptions { IndexName = "test-index" };
        o.TopK.Should().BePositive();
    }

    [Fact]
    public void GraphRagOptions_Default_SearchModeIsHybrid()
    {
        var o = new GraphRagOptions { IndexName = "test-index" };
        o.SearchMode.Should().Be(GraphRagSearchMode.Hybrid);
    }

    [Fact]
    public void GraphRagOptions_Default_FilterStopWordsIsTrue()
    {
        var o = new GraphRagOptions { IndexName = "test-index" };
        o.FilterStopWords.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Neo4j.AgentMemory.Enrichment
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void EnrichmentOptions_Default_WikipediaLanguageIsEn()
    {
        new EnrichmentOptions().WikipediaLanguage.Should().Be("en");
    }

    [Fact]
    public void EnrichmentOptions_Default_TimeoutSecondsIsPositive()
    {
        new EnrichmentOptions().TimeoutSeconds.Should().BePositive();
    }

    [Fact]
    public void EnrichmentOptions_Default_MaxRetriesIsNonNegative()
    {
        new EnrichmentOptions().MaxRetries.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void EnrichmentCacheOptions_Default_GeocodingCacheDurationIsPositive()
    {
        new EnrichmentCacheOptions().GeocodingCacheDuration.Should().BePositive();
    }

    [Fact]
    public void EnrichmentCacheOptions_Default_EnrichmentCacheDurationIsPositive()
    {
        new EnrichmentCacheOptions().EnrichmentCacheDuration.Should().BePositive();
    }

    [Fact]
    public void GeocodingOptions_Default_UserAgentIsNotEmpty()
    {
        new GeocodingOptions().UserAgent.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GeocodingOptions_Default_BaseUrlIsNotEmpty()
    {
        new GeocodingOptions().BaseUrl.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GeocodingOptions_Default_TimeoutSecondsIsPositive()
    {
        new GeocodingOptions().TimeoutSeconds.Should().BePositive();
    }

    [Fact]
    public void GeocodingOptions_Default_RateLimitPerSecondIsPositive()
    {
        new GeocodingOptions().RateLimitPerSecond.Should().BePositive();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Neo4j.AgentMemory.AgentFramework
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void AgentFrameworkOptions_Default_ContextFormatIsNotNull()
    {
        new AgentFrameworkOptions().ContextFormat.Should().NotBeNull();
    }

    [Fact]
    public void AgentFrameworkOptions_Default_AutoExtractOnPersistIsTrue()
    {
        new AgentFrameworkOptions().AutoExtractOnPersist.Should().BeTrue();
    }

    [Fact]
    public void AgentFrameworkOptions_Default_PersistReasoningTracesIsFalse()
    {
        new AgentFrameworkOptions().PersistReasoningTraces.Should().BeFalse();
    }

    [Fact]
    public void AgentFrameworkOptions_Default_SessionIdKeyIsNotEmpty()
    {
        new AgentFrameworkOptions().DefaultSessionIdKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AgentFrameworkOptions_Default_ConversationIdKeyIsNotEmpty()
    {
        new AgentFrameworkOptions().DefaultConversationIdKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ContextFormatOptions_Default_IncludeEntitiesIsTrue()
    {
        new ContextFormatOptions().IncludeEntities.Should().BeTrue();
    }

    [Fact]
    public void ContextFormatOptions_Default_IncludeFactsIsTrue()
    {
        new ContextFormatOptions().IncludeFacts.Should().BeTrue();
    }

    [Fact]
    public void ContextFormatOptions_Default_IncludePreferencesIsTrue()
    {
        new ContextFormatOptions().IncludePreferences.Should().BeTrue();
    }

    [Fact]
    public void ContextFormatOptions_Default_IncludeReasoningTracesIsFalse()
    {
        new ContextFormatOptions().IncludeReasoningTraces.Should().BeFalse();
    }

    [Fact]
    public void ContextFormatOptions_Default_ContextPrefixIsNotEmpty()
    {
        new ContextFormatOptions().ContextPrefix.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ContextFormatOptions_Default_MaxContextMessagesIsPositive()
    {
        new ContextFormatOptions().MaxContextMessages.Should().BePositive();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Neo4j.AgentMemory.McpServer
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void McpServerOptions_Default_ServerNameIsNotEmpty()
    {
        new McpServerOptions().ServerName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void McpServerOptions_Default_ServerVersionIsNotEmpty()
    {
        new McpServerOptions().ServerVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void McpServerOptions_Default_EnableGraphQueryIsFalse()
    {
        new McpServerOptions().EnableGraphQuery.Should().BeFalse();
    }

    [Fact]
    public void McpServerOptions_Default_DefaultSessionIdIsNotEmpty()
    {
        new McpServerOptions().DefaultSessionId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void McpServerOptions_Default_DefaultConfidenceIsInValidRange()
    {
        var o = new McpServerOptions();
        o.DefaultConfidence.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(1.0);
    }
}
