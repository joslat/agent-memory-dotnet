using System.Collections.Concurrent;
using AgentMemory.Native.Abi;
using AgentMemory.Native.Composition;
using AgentMemory.Native.Contract;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Native.Runtime;

/// <summary>
/// The process-wide table of open contexts, and the authority rule: one context per store and database at a time.
/// Handles count up from a random start and are never reused, so a stale handle can only be refused; context and job
/// handles carry different top bytes, so one is never mistaken for the other.
/// </summary>
internal static class ContextRegistry
{
    private static readonly ConcurrentDictionary<ulong, NativeContext> Contexts = new();
    private static readonly Dictionary<string, ulong> Authorities = new(StringComparer.Ordinal);
    private static readonly object AuthorityGate = new();
    private static long _nextContext = Seed(0x0A);
    private static long _nextJob = Seed(0x0B);

    /// <summary>How long <c>am_v1_open</c> waits for the store to answer when it verifies connectivity.</summary>
    private static readonly TimeSpan ConnectivityTimeout = TimeSpan.FromSeconds(15);

    public static string LibraryVersion { get; } =
        typeof(ContextRegistry).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static long Seed(byte tag) => ((long)tag << 56) | (Random.Shared.NextInt64(1L << 40) << 8);

    public static ulong NextJobHandle() => (ulong)Interlocked.Increment(ref _nextJob);

    public static bool TryGet(ulong handle, out NativeContext context) => Contexts.TryGetValue(handle, out context!);

    public static (int Status, ulong Handle, byte[]? Error) Open(uint requestedAbi, byte[] configuration)
    {
        if (requestedAbi >> 16 != AbiVersion.Major)
            return (AbiStatus.UnsupportedAbi, 0, ResultWriter.OpenError("unsupported_abi",
                $"This library implements ABI {AbiVersion.Major}.{AbiVersion.Minor}; ABI {requestedAbi >> 16}.{requestedAbi & 0xFFFF} was requested."));

        OpenConfig config;
        try
        {
            config = OpenConfig.Parse(configuration);
        }
        catch (ContractException invalid)
        {
            return (AbiStatus.InvalidArgument, 0, ResultWriter.OpenError(invalid.Code, invalid.Message));
        }

        var authority = config.AuthorityKey;
        lock (AuthorityGate)
        {
            if (!Authorities.TryAdd(authority, 0))
                return (AbiStatus.Busy, 0, ResultWriter.OpenError("authority_held",
                    "A context for this store and database is already open in this process; close it first."));
        }

        ServiceProvider? provider = null;
        try
        {
            provider = MemoryComposition.Build(config.Memory);
            if (config.VerifyConnectivity)
                provider.GetRequiredService<IDriver>().VerifyConnectivityAsync()
                    .WaitAsync(ConnectivityTimeout).GetAwaiter().GetResult();

            var handle = (ulong)Interlocked.Increment(ref _nextContext);
            var embeddings = config.EmbeddingEndpoint is { } endpoint
                ? new Embeddings.OpenAiCompatibleEmbeddingGenerator(endpoint, config.Memory.EmbeddingDimensions)
                : null;
            Contexts[handle] = new NativeContext(handle, config, provider, embeddings);
            lock (AuthorityGate)
            {
                Authorities[authority] = handle;
            }
            return (AbiStatus.Ok, handle, null);
        }
        catch (Exception exception)
        {
            lock (AuthorityGate)
            {
                Authorities.Remove(authority);
            }
            DisposeQuietly(provider);
            var (_, code, message) = exception is TimeoutException
                ? ("unavailable", "store_unavailable", $"The store did not answer within {ConnectivityTimeout.TotalSeconds:0} s.")
                : NativeContext.Classify(exception);
            return (AbiStatus.InternalError, 0, ResultWriter.OpenError(code, message));
        }
    }

    public static int Close(ulong handle)
    {
        if (!Contexts.TryGetValue(handle, out var context)) return AbiStatus.InvalidHandle;
        if (!context.BeginClose()) return AbiStatus.Busy;
        if (!Contexts.TryRemove(handle, out _)) return AbiStatus.InvalidHandle;
        try
        {
            context.Dispose();
        }
        finally
        {
            lock (AuthorityGate)
            {
                if (Authorities.TryGetValue(context.Config.AuthorityKey, out var holder) && holder == handle)
                    Authorities.Remove(context.Config.AuthorityKey);
            }
        }
        return AbiStatus.Ok;
    }

    private static void DisposeQuietly(ServiceProvider? provider)
    {
        if (provider is null) return;
        try
        {
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // The open already failed and reports that failure; a second one while cleaning up adds nothing.
        }
    }
}
