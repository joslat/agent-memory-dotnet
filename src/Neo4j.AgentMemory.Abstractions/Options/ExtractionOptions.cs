namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>Configuration for the extraction pipeline.</summary>
public sealed class ExtractionOptions
{
    /// <summary>Entity resolution strategy options.</summary>
    public EntityResolutionOptions EntityResolution { get; set; } = new();
    /// <summary>Entity validation filter options.</summary>
    public EntityValidationOptions Validation { get; set; } = new();
    /// <summary>Minimum confidence to accept an extracted entity.</summary>
    public double MinConfidenceThreshold { get; set; } = 0.5;
    /// <summary>When true, entities above <see cref="AutoMergeThreshold"/> are automatically merged.</summary>
    public bool EnableAutoMerge { get; set; } = true;
    /// <summary>Confidence threshold that triggers auto-merge (≥ this value).</summary>
    public double AutoMergeThreshold { get; set; } = 0.95;
    /// <summary>Confidence threshold that adds a SAME_AS relationship (between this and <see cref="AutoMergeThreshold"/>).</summary>
    public double SameAsThreshold { get; set; } = 0.85;
}

/// <summary>Controls which matching strategies are used for entity resolution.</summary>
public sealed class EntityResolutionOptions
{
    /// <summary>Enable exact name matching.</summary>
    public bool EnableExactMatch { get; set; } = true;
    /// <summary>Enable fuzzy (edit-distance) name matching.</summary>
    public bool EnableFuzzyMatch { get; set; } = true;
    /// <summary>Enable semantic (embedding) matching.</summary>
    public bool EnableSemanticMatch { get; set; } = true;
    /// <summary>When true, only match candidates of the same entity type.</summary>
    public bool TypeStrictFiltering { get; set; } = true;
    /// <summary>Minimum similarity score for a fuzzy match to be considered.</summary>
    public double FuzzyMatchThreshold { get; set; } = 0.85;
    /// <summary>Minimum cosine similarity for a semantic match to be considered.</summary>
    public double SemanticMatchThreshold { get; set; } = 0.8;
}

/// <summary>Controls validation rules applied to extracted entity candidates.</summary>
public sealed class EntityValidationOptions
{
    /// <summary>Minimum character length for an entity name to be accepted.</summary>
    public int MinNameLength { get; set; } = 2;
    /// <summary>Reject entities whose names consist entirely of digits.</summary>
    public bool RejectNumericOnly { get; set; } = true;
    /// <summary>Reject entities whose names consist entirely of punctuation.</summary>
    public bool RejectPunctuationOnly { get; set; } = true;
    /// <summary>Reject entities whose names are common stop-words.</summary>
    public bool UseStopwordFilter { get; set; } = true;
}
