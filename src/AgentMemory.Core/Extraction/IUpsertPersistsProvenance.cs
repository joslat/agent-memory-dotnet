namespace AgentMemory.Core.Extraction;

/// <summary>
/// Internal capability marker for repositories whose upsert operation atomically persists every
/// <c>EXTRACTED_FROM</c> edge named by the memory item's source-message IDs.
/// </summary>
internal interface IUpsertPersistsProvenance;
