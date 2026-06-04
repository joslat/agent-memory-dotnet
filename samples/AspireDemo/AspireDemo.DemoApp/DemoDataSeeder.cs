using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;

internal static class DemoDataSeeder
{
    public const string SessionId = "aspire-demo-session";
    public const string ConversationId = "aspire-demo-conversation";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 4, 30, 23, 44, 48, 278, TimeSpan.FromHours(2));

    public static async Task SeedAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var shortTerm = services.GetRequiredService<IShortTermMemoryService>();
        var longTerm = services.GetRequiredService<ILongTermMemoryService>();
        var entityRepository = services.GetRequiredService<IEntityRepository>();
        var factRepository = services.GetRequiredService<IFactRepository>();
        var preferenceRepository = services.GetRequiredService<IPreferenceRepository>();

        logger.LogInformation("Resetting scripted demo session '{SessionId}'", SessionId);
        await shortTerm.ClearSessionAsync(SessionId, cancellationToken);

        foreach (var preferenceId in Preferences.Select(static p => p.PreferenceId))
            await preferenceRepository.DeleteAsync(preferenceId, cancellationToken);

        foreach (var factId in Facts.Select(static f => f.FactId))
            await factRepository.DeleteAsync(factId, cancellationToken);

        foreach (var entityId in Entities.Select(static e => e.EntityId))
            await entityRepository.DeleteAsync(entityId, cancellationToken);

        await shortTerm.AddConversationAsync(
            ConversationId,
            SessionId,
            userId: "aspire-demo-user",
            metadata: new Dictionary<string, object>
            {
                ["scenario"] = "scripted",
                ["seed"] = "deterministic"
            },
            cancellationToken: cancellationToken);

        await shortTerm.AddMessagesAsync(Messages, cancellationToken);

        foreach (var entity in Entities)
            await longTerm.AddEntityAsync(entity, cancellationToken);

        foreach (var fact in Facts)
            await longTerm.AddFactAsync(fact, cancellationToken);

        foreach (var preference in Preferences)
            await longTerm.AddPreferenceAsync(preference, cancellationToken);

        foreach (var relationship in Relationships)
            await longTerm.AddRelationshipAsync(relationship, cancellationToken);

        logger.LogInformation(
            "Seeded {MessageCount} messages, {EntityCount} entities, {FactCount} facts, {PreferenceCount} preferences, {RelationshipCount} relationships.",
            Messages.Count,
            Entities.Count,
            Facts.Count,
            Preferences.Count,
            Relationships.Count);
    }

    private static IReadOnlyList<Message> Messages { get; } =
    [
        CreateMessage(
            "message-001",
            "system",
            "You are coordinating a deterministic memory demo for the Apex Crew.",
            0),
        CreateMessage(
            "message-002",
            "user",
            "Nova here. I own infrastructure notes and I prefer dark mode dashboards.",
            1),
        CreateMessage(
            "message-003",
            "assistant",
            "Logged it: Nova prefers dark mode dashboards and concise infrastructure notes.",
            2),
        CreateMessage(
            "message-004",
            "user",
            "Mira from operations needs deployment summaries before 09:00 UTC every day.",
            3),
        CreateMessage(
            "message-005",
            "assistant",
            "Captured: Mira wants deployment summaries before 09:00 UTC.",
            4),
        CreateMessage(
            "message-006",
            "user",
            "Sol pairs with Nova on recall ranking and insists every demo stays deterministic.",
            5)
    ];

    private static IReadOnlyList<Entity> Entities { get; } =
    [
        new Entity
        {
            EntityId = "entity-apex-crew",
            Name = "Apex Crew",
            CanonicalName = "Apex Crew",
            Type = EntityType.Organization,
            Description = "Three fictional agents used in the Aspire demo.",
            Confidence = 1.0,
            Aliases = ["The Crew"],
            SourceMessageIds = ["message-001"],
            CreatedAtUtc = SeedTimestamp
        },
        new Entity
        {
            EntityId = "entity-nova",
            Name = "Nova",
            CanonicalName = "Nova",
            Type = EntityType.Person,
            Description = "Infrastructure-focused agent who likes concise notes.",
            Confidence = 1.0,
            SourceMessageIds = ["message-002", "message-003"],
            CreatedAtUtc = SeedTimestamp.AddMinutes(1)
        },
        new Entity
        {
            EntityId = "entity-mira",
            Name = "Mira",
            CanonicalName = "Mira",
            Type = EntityType.Person,
            Description = "Operations agent who expects early deployment summaries.",
            Confidence = 1.0,
            SourceMessageIds = ["message-004", "message-005"],
            CreatedAtUtc = SeedTimestamp.AddMinutes(2)
        },
        new Entity
        {
            EntityId = "entity-sol",
            Name = "Sol",
            CanonicalName = "Sol",
            Type = EntityType.Person,
            Description = "Recall-ranking partner who requires deterministic demos.",
            Confidence = 1.0,
            SourceMessageIds = ["message-006"],
            CreatedAtUtc = SeedTimestamp.AddMinutes(3)
        }
    ];

    private static IReadOnlyList<Fact> Facts { get; } =
    [
        new Fact
        {
            FactId = "fact-nova-role",
            Subject = "Nova",
            Predicate = "owns",
            Object = "infrastructure notes",
            Category = "role",
            Confidence = 1.0,
            SourceMessageIds = ["message-002"],
            CreatedAtUtc = SeedTimestamp.AddMinutes(4)
        },
        new Fact
        {
            FactId = "fact-mira-summary-time",
            Subject = "Mira",
            Predicate = "needs",
            Object = "deployment summaries before 09:00 UTC",
            Category = "operations",
            Confidence = 1.0,
            SourceMessageIds = ["message-004", "message-005"],
            CreatedAtUtc = SeedTimestamp.AddMinutes(5)
        },
        new Fact
        {
            FactId = "fact-sol-focus",
            Subject = "Sol",
            Predicate = "owns",
            Object = "recall ranking",
            Category = "role",
            Confidence = 1.0,
            SourceMessageIds = ["message-006"],
            CreatedAtUtc = SeedTimestamp.AddMinutes(6)
        }
    ];

    private static IReadOnlyList<Preference> Preferences { get; } =
    [
        new Preference
        {
            PreferenceId = "preference-nova-dark-mode",
            Category = "ui",
            PreferenceText = "Prefer dark mode dashboards.",
            Context = "Infrastructure review workspace",
            Confidence = 1.0,
            SourceMessageIds = ["message-002", "message-003"],
            CreatedAtUtc = SeedTimestamp.AddMinutes(7)
        },
        new Preference
        {
            PreferenceId = "preference-mira-summary-time",
            Category = "reporting",
            PreferenceText = "Deployment summaries before 09:00 UTC.",
            Context = "Daily operations briefing",
            Confidence = 1.0,
            SourceMessageIds = ["message-004", "message-005"],
            CreatedAtUtc = SeedTimestamp.AddMinutes(8)
        },
        new Preference
        {
            PreferenceId = "preference-sol-deterministic",
            Category = "demo",
            PreferenceText = "Keep every demo deterministic.",
            Context = "Recall ranking walkthroughs",
            Confidence = 1.0,
            SourceMessageIds = ["message-006"],
            CreatedAtUtc = SeedTimestamp.AddMinutes(9)
        }
    ];

    private static IReadOnlyList<Relationship> Relationships { get; } =
    [
        new Relationship
        {
            RelationshipId = "relationship-nova-apex",
            SourceEntityId = "entity-nova",
            TargetEntityId = "entity-apex-crew",
            RelationshipType = "MEMBER_OF",
            Description = "Nova is part of the Apex Crew demo team.",
            Confidence = 1.0,
            SourceMessageIds = ["message-001", "message-002"],
            CreatedAtUtc = SeedTimestamp.AddMinutes(10)
        },
        new Relationship
        {
            RelationshipId = "relationship-mira-apex",
            SourceEntityId = "entity-mira",
            TargetEntityId = "entity-apex-crew",
            RelationshipType = "MEMBER_OF",
            Description = "Mira is part of the Apex Crew demo team.",
            Confidence = 1.0,
            SourceMessageIds = ["message-001", "message-004"],
            CreatedAtUtc = SeedTimestamp.AddMinutes(11)
        },
        new Relationship
        {
            RelationshipId = "relationship-sol-nova",
            SourceEntityId = "entity-sol",
            TargetEntityId = "entity-nova",
            RelationshipType = "PAIRS_WITH",
            Description = "Sol pairs with Nova on recall ranking.",
            Confidence = 1.0,
            SourceMessageIds = ["message-006"],
            CreatedAtUtc = SeedTimestamp.AddMinutes(12)
        }
    ];

    private static Message CreateMessage(string messageId, string role, string content, int minuteOffset) =>
        new()
        {
            MessageId = messageId,
            ConversationId = ConversationId,
            SessionId = SessionId,
            Role = role,
            Content = content,
            TimestampUtc = SeedTimestamp.AddMinutes(minuteOffset),
            Metadata = new Dictionary<string, object>
            {
                ["scenario"] = "scripted",
                ["order"] = minuteOffset + 1
            }
        };
}
