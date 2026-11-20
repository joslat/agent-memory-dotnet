using AgentMemory.Native.Composition;

namespace AgentMemory.Native.Contract;

/// <summary>Whether the context reads one configured owner, or the owner each request names.</summary>
internal enum OwnerMode
{
    Fixed,
    PerRequest,
}

/// <summary>A validated <c>am_v1_open</c> configuration.</summary>
internal sealed record OpenConfig
{
    public required MemorySettings Memory { get; init; }
    public required OwnerMode OwnerMode { get; init; }
    public string? FixedOwner { get; init; }
    public string? EmbeddingModel { get; init; }
    public bool VerifyConnectivity { get; init; } = true;
    public int MaxRunningJobs { get; init; } = 4;
    public int MaxPendingJobs { get; init; } = 32;
    public int DefaultTimeoutMs { get; init; } = 2000;

    /// <summary>One context per store and database in a process.</summary>
    public string AuthorityKey => $"{Memory.Uri.Trim().ToLowerInvariant()}|{Memory.Database}";

    public static OpenConfig Parse(ReadOnlyMemory<byte> utf8)
    {
        var (document, root) = StrictObject.Parse(utf8, "configuration");
        using (document)
        {
            if (root.OptionalInt("schemaVersion", 0, 0, int.MaxValue) != 1)
                throw new ContractException("unsupported_schema", "$.schemaVersion must be 1.");
            var profile = root.RequiredString("profile", 64);
            if (profile != "read_only")
                throw new ContractException("unsupported_profile", $"$.profile '{profile}' is not offered; this library offers 'read_only'.");

            var owner = root.RequiredObject("owner");
            var mode = owner.RequiredString("mode", 32) switch
            {
                "fixed" => OwnerMode.Fixed,
                "per_request" => OwnerMode.PerRequest,
                var other => throw new ContractException("invalid_value", $"$.owner.mode '{other}' must be 'fixed' or 'per_request'."),
            };
            var fixedOwner = mode == OwnerMode.Fixed ? owner.RequiredString("id", 256) : null;
            owner.EnsureNoUnknown();

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
            if (!Uri.TryCreate(uri, UriKind.Absolute, out _))
                throw new ContractException("invalid_value", "$.store.uri must be an absolute URI such as bolt://host:7687.");

            var embedding = root.RequiredObject("embedding");
            var dimensions = embedding.OptionalInt("dimensions", 0, 1, 8192);
            if (dimensions == 0)
                throw new ContractException("missing_property", "$.embedding.dimensions is required.");
            var model = embedding.OptionalString("model", 256);
            embedding.EnsureNoUnknown();

            var limits = root.OptionalObject("limits");
            var maxRunning = limits?.OptionalInt("maxRunningJobs", 4, 1, 64) ?? 4;
            var maxPending = limits?.OptionalInt("maxPendingJobs", 32, 1, 1024) ?? 32;
            var defaultTimeout = limits?.OptionalInt("defaultTimeoutMs", 2000, 1, 600_000) ?? 2000;
            limits?.EnsureNoUnknown();
            if (maxPending < maxRunning)
                throw new ContractException("invalid_value", "$.limits.maxPendingJobs must be at least maxRunningJobs.");

            root.EnsureNoUnknown();
            return new OpenConfig
            {
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
                VerifyConnectivity = verify,
                MaxRunningJobs = maxRunning,
                MaxPendingJobs = maxPending,
                DefaultTimeoutMs = defaultTimeout,
            };
        }
    }
}

/// <summary>A validated request: the operation and its arguments.</summary>
internal abstract record NativeRequest
{
    public string? RequestId { get; init; }
    public abstract string Operation { get; }

    public static NativeRequest Parse(ReadOnlyMemory<byte> utf8, OpenConfig config)
    {
        var (document, root) = StrictObject.Parse(utf8, "request");
        using (document)
        {
            if (root.OptionalInt("schemaVersion", 0, 0, int.MaxValue) != 1)
                throw new ContractException("unsupported_schema", "$.schemaVersion must be 1.");
            var operation = root.RequiredString("operation", 64);
            var requestId = root.OptionalString("requestId", 256);
            NativeRequest request = operation switch
            {
                CapabilitiesRequest.Name => new CapabilitiesRequest { RequestId = requestId },
                GetContextRequest.Name => GetContextRequest.Read(root, config, requestId),
                _ => throw new ContractException("unknown_operation",
                    $"Operation '{operation}' is not offered; this library offers '{CapabilitiesRequest.Name}' and '{GetContextRequest.Name}'."),
            };
            root.EnsureNoUnknown();
            return request;
        }
    }
}

/// <summary><c>capabilities_get</c>.</summary>
internal sealed record CapabilitiesRequest : NativeRequest
{
    public const string Name = "capabilities_get";
    public override string Operation => Name;
}

/// <summary><c>memory_get_context</c>: owner-scoped recall of facts and preferences by a supplied vector.</summary>
internal sealed record GetContextRequest : NativeRequest
{
    public const string Name = "memory_get_context";
    public override string Operation => Name;

    public required string Owner { get; init; }
    public required string SessionId { get; init; }
    public required float[] QueryVector { get; init; }
    public int MaxFacts { get; init; } = 5;
    public int MaxPreferences { get; init; } = 5;
    public double MinScore { get; init; } = 0.7;

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
        return new GetContextRequest
        {
            RequestId = requestId,
            Owner = owner,
            SessionId = root.RequiredString("sessionId", 256),
            QueryVector = root.RequiredVector("queryVector", config.Memory.EmbeddingDimensions),
            MaxFacts = root.OptionalInt("maxFacts", 5, 0, 50),
            MaxPreferences = root.OptionalInt("maxPreferences", 5, 0, 50),
            MinScore = root.OptionalDouble("minScore", 0.7, 0.0, 1.0),
        };
    }
}
