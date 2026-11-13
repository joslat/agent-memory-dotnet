using System.Collections.ObjectModel;
using AgentMemory.Abstractions.Options;
using Microsoft.Extensions.Options;

namespace AgentMemory.Core.Resolution;

/// <summary>A validated snapshot; asynchronous work never reads mutable host option collections.</summary>
internal sealed record IdentityConfiguration
{
    public bool Enabled { get; private init; }
    public int MaxCandidates { get; private init; }
    public int MaxMentionsPerIngestion { get; private init; }
    public int MaxContextUtf8Bytes { get; private init; }
    public int MaxAdjudicationsPerIngestion { get; private init; }
    public int AdjudicatorMaxOutputTokens { get; private init; }
    public TimeSpan AdjudicationTimeout { get; private init; }
    private IReadOnlyDictionary<string, EntityIdentityPolicy> Policies { get; init; } =
        new ReadOnlyDictionary<string, EntityIdentityPolicy>(new Dictionary<string, EntityIdentityPolicy>());
    private static readonly EntityIdentityPolicy DefaultPolicy = new();

    public EntityIdentityPolicy PolicyFor(string type) => Policies.TryGetValue(type, out var policy) ? policy : DefaultPolicy;

    public bool HasIdentityProperties => Policies.Values.Any(policy => policy.IdentityProperties.Count > 0);
    public bool RequiresCaseInsensitiveIdentityNames => Policies.Values.Any(policy => policy.IdentityNameComparison == StringComparison.OrdinalIgnoreCase);
    public bool RequiresContextRootedLookup => Policies.Values.Any(policy => policy.ReviewContextRootedCandidates);

    /// <summary>Captures options that were switched on explicitly (<see cref="IdentityResolutionOptions.Enabled"/> true).</summary>
    public static IdentityConfiguration Capture(IdentityResolutionOptions options) =>
        Capture(options ?? throw new ArgumentNullException(nameof(options)), options.Enabled == true, contextRootedLookup: false);

    /// <summary>
    /// Captures options for a host where identity runs, or not (<see cref="IdentityActivation"/>). Automatic mode
    /// (<see cref="IdentityResolutionOptions.Enabled"/> null) is the measured configuration: the conversational policies
    /// for any of their types the host left unset, nominating context-turn candidates only where the backend can look
    /// them up (<paramref name="contextRootedLookup"/>).
    /// </summary>
    public static IdentityConfiguration Capture(IdentityResolutionOptions options, bool active, bool contextRootedLookup)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!active) return new IdentityConfiguration();
        var errors = new List<string>();
        if (options.MaxMentionsPerIngestion is < 1 or > 256) errors.Add("MaxMentionsPerIngestion must be between 1 and 256.");
        if (options.MaxCandidates is < 1 or > 64) errors.Add("MaxCandidates must be between 1 and 64.");
        if (options.MaxContextUtf8Bytes is < 0 or > 1_048_576) errors.Add("MaxContextUtf8Bytes must be between 0 and 1048576.");
        if (options.MaxAdjudicationsPerIngestion is < 0 or > 64) errors.Add("MaxAdjudicationsPerIngestion must be between 0 and 64.");
        if (options.AdjudicatorMaxOutputTokens is < 1 or > 32_768) errors.Add("AdjudicatorMaxOutputTokens must be between 1 and 32768.");
        if (options.AdjudicationTimeout <= TimeSpan.Zero || options.AdjudicationTimeout > TimeSpan.FromMinutes(5))
            errors.Add("AdjudicationTimeout must be positive and at most five minutes.");
        if (options.ContextTurns is < 0 or > 32) errors.Add("ContextTurns must be between 0 and 32.");
        var policies = new Dictionary<string, EntityIdentityPolicy>(StringComparer.OrdinalIgnoreCase);
        if (options.Policies is null) errors.Add("Policies cannot be null.");
        else foreach (var (type, policy) in options.Policies)
        {
            if (string.IsNullOrWhiteSpace(type) || policy is null)
            {
                errors.Add("Every policy needs a type and a non-null value.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(policy.Version)) errors.Add($"Policy {type} needs a version.");
            if (policy.IdentityProperties is null || policy.IdentityProperties.Any(string.IsNullOrWhiteSpace) ||
                policy.IdentityProperties.Distinct(StringComparer.Ordinal).Count() != policy.IdentityProperties.Count)
                errors.Add($"Policy {type} needs distinct non-empty identity property names.");
            if (!Threshold(policy.FuzzyReviewThreshold) || !Threshold(policy.SemanticReviewThreshold) || !Threshold(policy.ContextReviewThreshold))
                errors.Add($"Policy {type} review thresholds must be finite and in [0,1].");
            if (policy.UniqueNameNamespace is not null && string.IsNullOrWhiteSpace(policy.UniqueNameNamespace))
                errors.Add($"Policy {type} has an empty unique-name namespace.");
            if (policy.CompatibleTypes is null || policy.CompatibleTypes.Any(string.IsNullOrWhiteSpace) ||
                policy.CompatibleTypes.Count > 8 || policy.CompatibleTypes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != policy.CompatibleTypes.Count)
                errors.Add($"Policy {type} supports at most eight distinct non-empty compatible types.");
            if (policy.ContextProperties is null || policy.ContextProperties.Any(string.IsNullOrWhiteSpace) ||
                policy.ContextProperties.Count > 32 || policy.ContextProperties.Distinct(StringComparer.Ordinal).Count() != policy.ContextProperties.Count)
                errors.Add($"Policy {type} supports at most 32 distinct non-empty context properties.");
            if (policy.IdentityNameComparison is not (StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase))
                errors.Add($"Policy {type} identity name comparison must be Ordinal or OrdinalIgnoreCase.");
            var snapshot = policy with
            {
                IdentityProperties = Array.AsReadOnly(policy.IdentityProperties?.ToArray() ?? []),
                CompatibleTypes = Array.AsReadOnly(policy.CompatibleTypes?.ToArray() ?? []),
                ContextProperties = Array.AsReadOnly(policy.ContextProperties?.ToArray() ?? [])
            };
            if (!policies.TryAdd(type, snapshot)) errors.Add($"Entity type {type} is configured more than once ignoring case.");
        }
        if (options.Enabled is null)
            foreach (var (type, policy) in new IdentityResolutionOptions().ApplyConversationalPolicies().Policies)
                policies.TryAdd(type, policy with { ReviewContextRootedCandidates = contextRootedLookup });
        if (errors.Count > 0) throw new OptionsValidationException(nameof(IdentityResolutionOptions), typeof(IdentityResolutionOptions), errors);
        return new IdentityConfiguration
        {
            Enabled = true, MaxCandidates = options.MaxCandidates, MaxMentionsPerIngestion = options.MaxMentionsPerIngestion, MaxContextUtf8Bytes = options.MaxContextUtf8Bytes,
            MaxAdjudicationsPerIngestion = options.MaxAdjudicationsPerIngestion,
            AdjudicatorMaxOutputTokens = options.AdjudicatorMaxOutputTokens, AdjudicationTimeout = options.AdjudicationTimeout,
            Policies = new ReadOnlyDictionary<string, EntityIdentityPolicy>(policies)
        };
    }

    private static bool Threshold(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}
