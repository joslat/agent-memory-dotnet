using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// Harness-only extractor overrides for LAB-P0. They intercept one explicit source marker and
/// delegate every other request to the real registered extractor.
/// </summary>
public static class FrozenExtractionOverrides
{
    public const string SourceMarker =
        "LAB-P0 frozen source: Rowan Vale works at Northstar P0 Labs and prefers terse status notes.";

    public static void Decorate(IServiceCollection services)
    {
        Decorate<IEntityExtractor>(services, inner => new FrozenEntityExtractor(inner));
        Decorate<IFactExtractor>(services, inner => new FrozenFactExtractor(inner));
        Decorate<IPreferenceExtractor>(services, inner => new FrozenPreferenceExtractor(inner));
        Decorate<IRelationshipExtractor>(services, inner => new FrozenRelationshipExtractor(inner));
    }

    public sealed class FrozenEntityExtractor(IEntityExtractor inner) : IEntityExtractor
    {
        public Task<IReadOnlyList<ExtractedEntity>> ExtractAsync(
            IReadOnlyList<Message> messages,
            CancellationToken cancellationToken = default) =>
            IsFrozen(messages)
                ? Task.FromResult<IReadOnlyList<ExtractedEntity>>(
                [
                    new()
                    {
                        Name = "Northstar P0 Labs",
                        Type = "ORGANIZATION",
                        Confidence = 0.92,
                    },
                    new()
                    {
                        Name = "Rowan Vale",
                        Type = "PERSON",
                        Confidence = 0.95,
                    },
                ])
                : inner.ExtractAsync(messages, cancellationToken);
    }

    public sealed class FrozenFactExtractor(IFactExtractor inner) : IFactExtractor
    {
        public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
            IReadOnlyList<Message> messages,
            CancellationToken cancellationToken = default) =>
            IsFrozen(messages)
                ? Task.FromResult<IReadOnlyList<ExtractedFact>>(
                [
                    new()
                    {
                        Subject = "Rowan Vale",
                        Predicate = "works_at",
                        Object = "Northstar P0 Labs",
                        Confidence = 0.90,
                    },
                    new()
                    {
                        Subject = "Rowan Vale",
                        Predicate = "leads",
                        Object = "cold-build acceleration",
                        Confidence = 0.85,
                    },
                ])
                : inner.ExtractAsync(messages, cancellationToken);
    }

    public sealed class FrozenPreferenceExtractor(IPreferenceExtractor inner) : IPreferenceExtractor
    {
        public Task<IReadOnlyList<ExtractedPreference>> ExtractAsync(
            IReadOnlyList<Message> messages,
            CancellationToken cancellationToken = default) =>
            IsFrozen(messages)
                ? Task.FromResult<IReadOnlyList<ExtractedPreference>>(
                [
                    new()
                    {
                        Category = "communication",
                        PreferenceText = "prefers terse status notes",
                        Confidence = 0.88,
                    },
                ])
                : inner.ExtractAsync(messages, cancellationToken);
    }

    public sealed class FrozenRelationshipExtractor(IRelationshipExtractor inner) : IRelationshipExtractor
    {
        public Task<IReadOnlyList<ExtractedRelationship>> ExtractAsync(
            IReadOnlyList<Message> messages,
            CancellationToken cancellationToken = default) =>
            IsFrozen(messages)
                ? Task.FromResult<IReadOnlyList<ExtractedRelationship>>(
                [
                    new()
                    {
                        SourceEntity = "Rowan Vale",
                        TargetEntity = "Northstar P0 Labs",
                        RelationshipType = "LAB_P0_WORKS_AT",
                        Confidence = 0.90,
                    },
                ])
                : inner.ExtractAsync(messages, cancellationToken);
    }

    private static bool IsFrozen(IReadOnlyList<Message> messages) =>
        messages.Any(message =>
            string.Equals(message.Content, SourceMarker, StringComparison.Ordinal));

    private static void Decorate<TService>(
        IServiceCollection services,
        Func<TService, TService> wrap)
        where TService : class
    {
        var descriptor = services.LastOrDefault(item => item.ServiceType == typeof(TService))
            ?? throw new InvalidOperationException(
                $"{typeof(TService).Name} was not registered before the LAB-P0 decorator.");

        services.Remove(descriptor);
        services.Add(new ServiceDescriptor(
            typeof(TService),
            provider => wrap(CreateService<TService>(provider, descriptor)),
            descriptor.Lifetime));
    }

    private static TService CreateService<TService>(
        IServiceProvider provider,
        ServiceDescriptor descriptor)
        where TService : class
    {
        if (descriptor.ImplementationInstance is TService instance)
            return instance;

        if (descriptor.ImplementationFactory is not null)
            return (TService)descriptor.ImplementationFactory(provider);

        if (descriptor.ImplementationType is not null)
            return (TService)ActivatorUtilities.CreateInstance(
                provider, descriptor.ImplementationType);

        throw new InvalidOperationException(
            $"{typeof(TService).Name} registration has no implementation.");
    }
}
