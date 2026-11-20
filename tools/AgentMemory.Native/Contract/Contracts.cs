using AgentMemory.Native.Chat;
using AgentMemory.Native.Composition;
using System.Text.Json;
using AgentMemory.Native.Embeddings;

namespace AgentMemory.Native.Contract;

/// <summary>Whether the context reads one configured owner, or the owner each request names.</summary>
internal enum OwnerMode
{
    Fixed,
    PerRequest,
}

/// <summary>What a context may do: read only, or also remember the owner's own words through the journal.</summary>
internal enum MemoryProfile
{
    ReadOnly,
    ExplicitWrite,

    /// <summary>R2: explicit writes plus observed ones, each applied once by its operation id (a receipt in the store).</summary>
    Learning,
}

/// <summary>A validated <c>am_v1_open</c> configuration.</summary>
internal sealed record OpenConfig
{
    public const string ReadOnlyName = "read_only";
    public const string ExplicitWriteName = "explicit_write";
    public const string LearningName = "learning";

    public MemoryProfile Profile { get; init; } = MemoryProfile.ReadOnly;
    public string ProfileName => Profile switch
    {
        MemoryProfile.ExplicitWrite => ExplicitWriteName,
        MemoryProfile.Learning => LearningName,
        _ => ReadOnlyName,
    };

    /// <summary>The profile writes (explicit_write or learning).</summary>
    public bool Writes => Profile != MemoryProfile.ReadOnly;

    /// <summary>The writer's model (explicit_write only).</summary>
    public ChatEndpoint? Writer { get; init; }

    /// <summary>Earlier turns of the session the writer reads (never extracts from); 0, the default, reads none.</summary>
    public int WriterContextTurns { get; init; }

    /// <summary>Where the journal lives (explicit_write only).</summary>
    public string? StateDirectory { get; init; }

    /// <summary>The most writes accepted and not yet finished (explicit_write only).</summary>
    public int MaxQueuedWrites { get; init; } = 16;

    public required MemorySettings Memory { get; init; }
    public required OwnerMode OwnerMode { get; init; }
    public string? FixedOwner { get; init; }
    public string? EmbeddingModel { get; init; }

    /// <summary>Where query text becomes a vector; null when every request must carry its vector.</summary>
    public EmbeddingEndpoint? EmbeddingEndpoint { get; init; }
    public bool VerifyConnectivity { get; init; } = true;
    public int MaxRunningJobs { get; init; } = 4;
    public int MaxPendingJobs { get; init; } = 32;
    public int DefaultTimeoutMs { get; init; } = 2000;

    private static readonly HashSet<string> StoreSchemes =
        new(["bolt", "bolt+s", "bolt+ssc", "neo4j", "neo4j+s", "neo4j+ssc"], StringComparer.OrdinalIgnoreCase);

    /// <summary>One context per store and database in a process.</summary>
    public string AuthorityKey => $"{Memory.Uri.Trim().ToLowerInvariant()}|{Memory.Database}";

    public static OpenConfig Parse(ReadOnlyMemory<byte> utf8)
    {
        var (document, root) = StrictObject.Parse(utf8, "configuration");
        using (document)
        try
        {
            if (root.OptionalInt("schemaVersion", 0, 0, int.MaxValue) != 1)
                throw new ContractException("unsupported_schema", "$.schemaVersion must be 1.");
            var profile = root.RequiredString("profile", 64) switch
            {
                ReadOnlyName => MemoryProfile.ReadOnly,
                ExplicitWriteName => MemoryProfile.ExplicitWrite,
                LearningName => MemoryProfile.Learning,
                var other => throw new ContractException("unsupported_profile",
                    $"$.profile '{other}' is not offered; this library offers '{ReadOnlyName}', '{ExplicitWriteName}' and '{LearningName}'."),
            };
            var writes = profile != MemoryProfile.ReadOnly;

            var owner = root.RequiredObject("owner");
            var mode = owner.RequiredString("mode", 32) switch
            {
                "fixed" => OwnerMode.Fixed,
                "per_request" => OwnerMode.PerRequest,
                var other => throw new ContractException("invalid_value", $"$.owner.mode '{other}' must be 'fixed' or 'per_request'."),
            };
            var fixedOwner = mode == OwnerMode.Fixed ? owner.RequiredString("id", 256) : null;
            owner.EnsureNoUnknown();
            if (writes && mode != OwnerMode.Fixed)
                throw new ContractException("owner_mode_not_offered",
                    $"$.owner.mode must be 'fixed' for a writing profile: one configured owner writes; several owners need their own identity design.");

            var store = root.RequiredObject("store");
            var uri = store.RequiredString("uri", 2048);
            var username = store.RequiredString("username", 256);
            var password = store.RequiredString("password", 1024);
            var database = store.OptionalString("database", 256) ?? "neo4j";
            var transactionTimeoutMs = store.OptionalInt("transactionTimeoutMs", 0, 0, 600_000);
            var verify = store.OptionalBool("verifyConnectivity", true);
            var connectionTimeoutMs = store.OptionalInt("connectionTimeoutMs", 3000, 1, 600_000);
            var acquisitionTimeoutMs = store.OptionalInt("connectionAcquisitionTimeoutMs", 5000, 1, 600_000);
            var maxRetryTimeMs = store.OptionalInt("maxRetryTimeMs", 1000, 0, 600_000);
            store.EnsureNoUnknown();
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var storeUri) || !StoreSchemes.Contains(storeUri.Scheme))
                throw new ContractException("invalid_value",
                    "$.store.uri must be a Neo4j URI: bolt://, bolt+s://, bolt+ssc://, neo4j://, neo4j+s:// or neo4j+ssc://.");

            var embedding = root.RequiredObject("embedding");
            var dimensions = embedding.OptionalInt("dimensions", 0, 1, 8192);
            if (dimensions == 0)
                throw new ContractException("missing_property", "$.embedding.dimensions is required.");
            var model = embedding.OptionalString("model", 256);
            EmbeddingEndpoint? endpoint = null;
            if (embedding.OptionalObject("endpoint") is { } endpointObject)
            {
                var endpointUrl = endpointObject.RequiredString("url", 2048);
                var apiKey = endpointObject.OptionalString("apiKey", 4096);
                var endpointTimeoutMs = endpointObject.OptionalInt("timeoutMs", 2000, 1, 600_000);
                endpointObject.EnsureNoUnknown();
                if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var parsedUrl)
                    || (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
                    throw new ContractException("invalid_value", "$.embedding.endpoint.url must be an absolute http or https URL.");
                if (model is null)
                    throw new ContractException("missing_property", "$.embedding.model is required with an endpoint: it names the model to ask for.");
                endpoint = new EmbeddingEndpoint(parsedUrl, model, apiKey, TimeSpan.FromMilliseconds(endpointTimeoutMs));
            }
            embedding.EnsureNoUnknown();

            ChatEndpoint? writer = null;
            var writerContextTurns = 0;
            string? stateDirectory = null;
            if (writes)
            {
                if (endpoint is null)
                    throw new ContractException("missing_property",
                        "$.embedding.endpoint is required for a writing profile: written memories are embedded by the library.");
                var writerObject = root.RequiredObject("writer");
                var writerModel = writerObject.RequiredString("model", 256);
                var writerReasoning = writerObject.OptionalString("reasoningEffort", 16);
                if (writerReasoning is not null && writerReasoning is not ("none" or "low" or "medium" or "high"))
                    throw new ContractException("invalid_value", "$.writer.reasoningEffort must be none, low, medium or high.");
                writerContextTurns = writerObject.OptionalInt("contextTurns", 0, 0, 32);
                var writerEndpoint = writerObject.RequiredObject("endpoint");
                var writerUrl = writerEndpoint.RequiredString("url", 2048);
                var writerKey = writerEndpoint.OptionalString("apiKey", 4096);
                var writerTimeoutMs = writerEndpoint.OptionalInt("timeoutMs", 60_000, 1, 600_000);
                writerEndpoint.EnsureNoUnknown();
                writerObject.EnsureNoUnknown();
                if (!Uri.TryCreate(writerUrl, UriKind.Absolute, out var parsedWriterUrl)
                    || (parsedWriterUrl.Scheme != Uri.UriSchemeHttp && parsedWriterUrl.Scheme != Uri.UriSchemeHttps))
                    throw new ContractException("invalid_value", "$.writer.endpoint.url must be an absolute http or https URL.");
                writer = new ChatEndpoint(parsedWriterUrl, writerModel, writerKey, TimeSpan.FromMilliseconds(writerTimeoutMs),
                    writerReasoning);

                var state = root.RequiredObject("state");
                stateDirectory = state.RequiredString("directory", 1024);
                state.EnsureNoUnknown();
                if (!Path.IsPathFullyQualified(stateDirectory))
                    throw new ContractException("invalid_value", "$.state.directory must be an absolute path.");
            }
            else
            {
                foreach (var name in new[] { "writer", "state" })
                    if (root.OptionalObject(name) is not null)
                        throw new ContractException("not_in_profile", $"$.{name} belongs to a writing profile; this profile is '{ReadOnlyName}'.");
            }

            var limits = root.OptionalObject("limits");
            var maxRunning = limits?.OptionalInt("maxRunningJobs", 4, 1, 64) ?? 4;
            var maxPending = limits?.OptionalInt("maxPendingJobs", 32, 1, 1024) ?? 32;
            var defaultTimeout = limits?.OptionalInt("defaultTimeoutMs", 2000, 1, 600_000) ?? 2000;
            var maxQueuedWrites = writes ? limits?.OptionalInt("maxQueuedWrites", 16, 1, 256) ?? 16 : 16;
            limits?.EnsureNoUnknown();
            if (maxPending < maxRunning)
                throw new ContractException("invalid_value", "$.limits.maxPendingJobs must be at least maxRunningJobs.");

            root.EnsureNoUnknown();
            return new OpenConfig
            {
                Profile = profile,
                Writer = writer,
                WriterContextTurns = writerContextTurns,
                StateDirectory = stateDirectory,
                MaxQueuedWrites = maxQueuedWrites,
                Memory = new MemorySettings
                {
                    Uri = uri,
                    Username = username,
                    Password = password,
                    Database = database,
                    EmbeddingDimensions = dimensions,
                    TransactionTimeout = transactionTimeoutMs > 0 ? TimeSpan.FromMilliseconds(transactionTimeoutMs) : null,
                    ConnectionTimeout = TimeSpan.FromMilliseconds(connectionTimeoutMs),
                    ConnectionAcquisitionTimeout = TimeSpan.FromMilliseconds(acquisitionTimeoutMs),
                    MaxTransactionRetryTime = TimeSpan.FromMilliseconds(maxRetryTimeMs),
                },
                OwnerMode = mode,
                FixedOwner = fixedOwner,
                EmbeddingModel = model,
                EmbeddingEndpoint = endpoint,
                VerifyConnectivity = verify,
                MaxRunningJobs = maxRunning,
                MaxPendingJobs = maxPending,
                DefaultTimeoutMs = defaultTimeout,
            };
        }
        catch (InvalidOperationException)
        {
            // Raised by the JSON reader only for text it cannot decode (a lone surrogate escape, which a
            // lookup by property name unescapes on its own path); every kind is checked before it is read.
            throw new ContractException("malformed_json", "The configuration holds text that is not valid Unicode.");
        }
    }
}

/// <summary>A validated request: the operation and its arguments.</summary>
internal abstract record NativeRequest
{
    public string? RequestId { get; init; }
    public abstract string Operation { get; }

    /// <summary>
    /// The operation and request id as far as they can be read, to label a result even when the request is refused.
    /// Never throws; strict validation is the job of Parse.
    /// </summary>
    public static (string? Operation, string? RequestId) Peek(ReadOnlyMemory<byte> utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            return (Label(document.RootElement, "operation", 64), Label(document.RootElement, "requestId", 256));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return (null, null);
        }

        static string? Label(JsonElement root, string name, int maxLength) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text && text.Length <= maxLength
                ? text
                : null;
    }

    public static NativeRequest Parse(ReadOnlyMemory<byte> utf8, OpenConfig config)
    {
        var (document, root) = StrictObject.Parse(utf8, "request");
        using (document)
        try
        {
            if (root.OptionalInt("schemaVersion", 0, 0, int.MaxValue) != 1)
                throw new ContractException("unsupported_schema", "$.schemaVersion must be 1.");
            var operation = root.RequiredString("operation", 64);
            var requestId = root.OptionalString("requestId", 256);
            var writes = config.Writes;
            NativeRequest request = operation switch
            {
                CapabilitiesRequest.Name => new CapabilitiesRequest { RequestId = requestId },
                GetContextRequest.Name => GetContextRequest.Read(root, config, requestId),
                RememberRequest.Name when writes => RememberRequest.Read(root, requestId, config.Profile == MemoryProfile.Learning),
                OperationGetRequest.Name when writes => OperationGetRequest.Read(root, requestId),
                OperationResolveRequest.Name when writes => OperationResolveRequest.Read(root, requestId),
                ForgetRequest.Name when writes => ForgetRequest.Read(root, requestId),
                EraseOwnerRequest.Name when writes => EraseOwnerRequest.Read(root, requestId),
                _ => throw new ContractException("unknown_operation",
                    $"Operation '{operation}' is not offered by profile '{config.ProfileName}'; it offers {string.Join(", ", Offered(config).Select(o => $"'{o}'"))}."),
            };
            root.EnsureNoUnknown();
            return request;
        }
        catch (InvalidOperationException)
        {
            // Raised by the JSON reader only for text it cannot decode (a lone surrogate escape, which a
            // lookup by property name unescapes on its own path); every kind is checked before it is read.
            throw new ContractException("malformed_json", "The request holds text that is not valid Unicode.");
        }
    }

    /// <summary>The operations a context of this configuration offers, in the order capabilities lists them.</summary>
    public static IReadOnlyList<string> Offered(OpenConfig config) => config.Writes
        ? [CapabilitiesRequest.Name, GetContextRequest.Name, RememberRequest.Name, OperationGetRequest.Name, OperationResolveRequest.Name,
            ForgetRequest.Name, EraseOwnerRequest.Name]
        : [CapabilitiesRequest.Name, GetContextRequest.Name];

    /// <summary>A fixed context's requests may not name an owner: the configured one is the only one.</summary>
    protected static void RefuseOwner(StrictObject root)
    {
        if (root.OptionalString("owner", 256) is not null)
            throw new ContractException("owner_not_allowed", "This context writes for one configured owner; a request may not name an owner.");
    }
}

/// <summary><c>memory_remember</c>: the owner's own words, to be remembered through the journal.</summary>
internal sealed record RememberRequest : NativeRequest
{
    public const string Name = "memory_remember";
    public override string Operation => Name;

    /// <summary>The host's stable identity for this spoken item and revision; a retry sends the same one.</summary>
    public required string SourceId { get; init; }
    public required string SessionId { get; init; }
    public required string Text { get; init; }

    /// <summary>When the words were said, with an explicit offset.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>"explicit" (the owner asked) or "observed" (learning profile: admitted without being asked).</summary>
    public string Origin { get; init; } = "explicit";

    public static RememberRequest Read(StrictObject root, string? requestId, bool learning = false)
    {
        RefuseOwner(root);
        var origin = root.OptionalString("origin", 16) ?? "explicit";
        if (origin is not ("explicit" or "observed"))
            throw new ContractException("invalid_value", "$.origin must be 'explicit' or 'observed'.");
        if (origin == "observed" && !learning)
            throw new ContractException("not_in_profile", "An observed remember needs the 'learning' profile; this one writes only what the owner asks for.");
        var occurred = root.RequiredString("occurredAt", 64);
        if (!(occurred.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(occurred, @"[+-]\d{2}:\d{2}$"))
            || !DateTimeOffset.TryParse(occurred, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var occurredAt))
            throw new ContractException("invalid_value",
                "$.occurredAt must be an ISO 8601 time with an explicit offset, such as 2026-11-20T10:00:00Z.");
        return new RememberRequest
        {
            RequestId = requestId,
            SourceId = root.RequiredString("sourceId", 256),
            SessionId = root.RequiredString("sessionId", 256),
            Text = root.RequiredString("text", 8000),
            OccurredAt = occurredAt,
            Origin = origin,
        };
    }
}

/// <summary><c>memory_operation_get</c>: the receipt of one operation, by its id or its source id.</summary>
internal sealed record OperationGetRequest : NativeRequest
{
    public const string Name = "memory_operation_get";
    public override string Operation => Name;

    public string? OperationId { get; init; }
    public string? SourceId { get; init; }

    public static OperationGetRequest Read(StrictObject root, string? requestId)
    {
        var operationId = root.OptionalString("operationId", 64);
        var sourceId = root.OptionalString("sourceId", 256);
        if ((operationId is null) == (sourceId is null))
            throw new ContractException("operation_required", "A request carries exactly one of $.operationId and $.sourceId.");
        return new OperationGetRequest { RequestId = requestId, OperationId = operationId, SourceId = sourceId };
    }
}

/// <summary><c>memory_operation_resolve</c>: ends a partial or unknown operation; R1 offers only "keep".</summary>
internal sealed record OperationResolveRequest : NativeRequest
{
    public const string Name = "memory_operation_resolve";
    public override string Operation => Name;

    public required string OperationId { get; init; }

    public static OperationResolveRequest Read(StrictObject root, string? requestId)
    {
        var operationId = root.RequiredString("operationId", 64);
        var resolution = root.RequiredString("resolution", 32);
        if (resolution != "keep")
            throw new ContractException("invalid_value",
                "$.resolution must be 'keep' (accept what the operation wrote, as it is); removing it arrives with the privacy controls.");
        return new OperationResolveRequest { RequestId = requestId, OperationId = operationId };
    }
}

/// <summary><c>capabilities_get</c>.</summary>
internal sealed record CapabilitiesRequest : NativeRequest
{
    public const string Name = "capabilities_get";
    public override string Operation => Name;
}

/// <summary><c>memory_get_context</c>: owner-scoped recall of facts and preferences by a supplied vector; on request also the
/// session's own earlier messages, entities, relationships and the facts of a time the question names ("yesterday").
/// Each extra section is off unless asked for, so an existing caller's recall is unchanged. Person and event details
/// are not offered: they need identity-evidence storage, which this profile has not.</summary>
internal sealed record GetContextRequest : NativeRequest
{
    public const string Name = "memory_get_context";
    public override string Operation => Name;

    public required string Owner { get; init; }
    public required string SessionId { get; init; }
    /// <summary>Exactly one of <see cref="QueryVector"/> and <see cref="QueryText"/> is set.</summary>
    public float[]? QueryVector { get; init; }
    public string? QueryText { get; init; }
    public int MaxFacts { get; init; } = 5;
    public int MaxPreferences { get; init; } = 5;
    public double MinScore { get; init; } = 0.7;
    /// <summary>The messages of this request's session most like the question.</summary>
    public int MaxMessages { get; init; }
    public int MaxEntities { get; init; }
    public int MaxRelationships { get; init; }
    /// <summary>The facts of a time the question names ("yesterday", "last week"), by their dates.</summary>
    public bool TimeWindows { get; init; }

    public static GetContextRequest Read(StrictObject root, OpenConfig config, string? requestId)
    {
        var named = root.OptionalString("owner", 256);
        var owner = config.OwnerMode switch
        {
            OwnerMode.Fixed when named is not null => throw new ContractException("owner_not_allowed",
                "This context reads one configured owner; a request may not name an owner."),
            OwnerMode.Fixed => config.FixedOwner!,
            _ => named ?? throw new ContractException("owner_required",
                "This context serves several owners; every request must name its owner."),
        };
        var text = root.OptionalString("query", 8000);
        var vector = root.OptionalVector("queryVector", config.Memory.EmbeddingDimensions);
        if ((text is null) == (vector is null))
            throw new ContractException("query_required", "A request carries exactly one of $.query (text) and $.queryVector.");
        if (text is not null && config.EmbeddingEndpoint is null)
            throw new ContractException("embedding_required",
                "This context has no embedding endpoint; send $.queryVector, or configure $.embedding.endpoint.");
        return new GetContextRequest
        {
            RequestId = requestId,
            Owner = owner,
            SessionId = root.RequiredString("sessionId", 256),
            QueryVector = vector,
            QueryText = text,
            MaxFacts = root.OptionalInt("maxFacts", 5, 0, 50),
            MaxPreferences = root.OptionalInt("maxPreferences", 5, 0, 50),
            MinScore = root.OptionalDouble("minScore", 0.7, 0.0, 1.0),
            MaxMessages = root.OptionalInt("maxMessages", 0, 0, 20),
            MaxEntities = root.OptionalInt("maxEntities", 0, 0, 50),
            MaxRelationships = root.OptionalInt("maxRelationships", 0, 0, 50),
            TimeWindows = root.OptionalBool("timeWindows", false),
        };
    }
}

/// <summary>
/// <c>memory_forget</c>: stop using what one remember produced (<c>operationId</c>), or the owner's facts and preferences
/// by id (<c>memoryIds</c>). Forgotten memories are closed, not deleted.
/// </summary>
internal sealed record ForgetRequest : NativeRequest
{
    public const string Name = "memory_forget";
    public override string Operation => Name;

    public string? TargetOperationId { get; init; }
    public IReadOnlyList<string>? MemoryIds { get; init; }

    public static ForgetRequest Read(StrictObject root, string? requestId)
    {
        RefuseOwner(root);
        var operationId = root.OptionalString("operationId", 64);
        var ids = root.OptionalStrings("memoryIds", 50, 256);
        if ((operationId is null) == (ids is null))
            throw new ContractException("target_required", "A request carries exactly one of $.operationId and $.memoryIds.");
        return new ForgetRequest { RequestId = requestId, TargetOperationId = operationId, MemoryIds = ids };
    }
}

/// <summary><c>memory_erase_owner</c>: delete everything of the configured owner. Needs <c>"confirm": true</c>.</summary>
internal sealed record EraseOwnerRequest : NativeRequest
{
    public const string Name = "memory_erase_owner";
    public override string Operation => Name;

    public static EraseOwnerRequest Read(StrictObject root, string? requestId)
    {
        RefuseOwner(root);
        if (!root.OptionalBool("confirm", false))
            throw new ContractException("confirmation_required",
                "$.confirm must be true: erasing the owner deletes everything of theirs and cannot be undone.");
        return new EraseOwnerRequest { RequestId = requestId };
    }
}