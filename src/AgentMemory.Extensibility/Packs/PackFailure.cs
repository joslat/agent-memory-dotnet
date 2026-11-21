// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Extensibility.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Extensibility.Packs;

/// <summary>
/// What a failed pack operation was doing: the pack, the operation, the step inside it, and a stable code a host can act on.
/// The library attaches it to the exception the operation throws without changing that exception's type, so existing
/// <c>catch</c> blocks still match; read it with <see cref="Of"/>.
/// </summary>
/// <remarks>
/// The innermost step that saw the failure names it: a schema refusal inside an import reads <c>pack.import.schema.refused</c>,
/// not a generic import failure.
/// </remarks>
[Experimental("AMEXT001")]
public sealed record PackFailure
{
    internal const string DataKey = "AgentMemory.PackFailure";
    private const string LoggedKey = "AgentMemory.PackFailure.Logged";

    /// <summary>Stable code: <c>pack.{operation}.{step}.{kind}</c>, for example <c>pack.import.schema.refused</c>.</summary>
    public required string Code { get; init; }
    /// <summary>The operation that failed (see <see cref="PackOperations"/>).</summary>
    public required string Operation { get; init; }
    /// <summary>The step inside the operation that failed (see <see cref="PackSteps"/>).</summary>
    public required string Step { get; init; }
    /// <summary>Why it failed, in one word (see <see cref="PackFailureKinds"/>).</summary>
    public required string Kind { get; init; }
    /// <summary>The pack id.</summary>
    public required string PackId { get; init; }
    /// <summary>The pack version, when the operation knew it.</summary>
    public string? Version { get; init; }
    /// <summary>The pack's bundle digest, when the operation knew it.</summary>
    public string? Digest { get; init; }

    /// <summary>The pack failure carried by an exception or any exception inside it; null when none was attached.</summary>
    public static PackFailure? Of(Exception? exception)
    {
        var pending = new Queue<Exception>();
        if (exception is not null) pending.Enqueue(exception);
        while (pending.TryDequeue(out var current))
        {
            if (current.Data[DataKey] is PackFailure failure) return failure;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Enqueue(inner);
            else if (current.InnerException is { } inner) pending.Enqueue(inner);
        }
        return null;
    }

    /// <summary>Attaches a failure unless one is already there (the innermost step wins), and returns the one that stays.</summary>
    internal static PackFailure Attach(Exception exception, string operation, string step, string packId, string? version, string? digest,
        string? kind = null)
    {
        if (Of(exception) is { } existing) return existing;
        kind ??= KindOf(exception);
        var failure = new PackFailure
        {
            Code = $"pack.{operation}.{step}.{kind}", Operation = operation, Step = step, Kind = kind,
            PackId = packId, Version = version, Digest = digest,
        };
        exception.Data[DataKey] = failure;
        return failure;
    }

    /// <summary>True the first time it is asked for an exception, so one failure writes one log line however many layers see it.</summary>
    internal static bool FirstLog(Exception exception)
    {
        if (exception.Data.Contains(LoggedKey)) return false;
        exception.Data[LoggedKey] = true;
        return true;
    }

    /// <summary>
    /// <see cref="PackFailureKinds.Unavailable"/> for a timeout or a Neo4j driver connection failure anywhere in the chain;
    /// <see cref="PackFailureKinds.Refused"/> for a pack the library refused; otherwise <see cref="PackFailureKinds.Failed"/>.
    /// </summary>
    internal static string KindOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            // By name: this assembly does not reference the driver.
            if (current is TimeoutException || current.GetType() is { Namespace: "Neo4j.Driver" } type
                && type.Name is "ServiceUnavailableException" or "SessionExpiredException" or "TransientException")
                return PackFailureKinds.Unavailable;
        }
        return exception is KnowledgePackException or ArgumentException or InvalidDataException
            ? PackFailureKinds.Refused : PackFailureKinds.Failed;
    }
}

/// <summary>The pack operations a <see cref="PackFailure"/> names.</summary>
[Experimental("AMEXT001")]
public static class PackOperations
{
    /// <summary>Importing a pack's schema and records.</summary>
    public const string Import = "import";
    /// <summary>Closing a pack's live records.</summary>
    public const string Remove = "remove";
    /// <summary>Applying a pack's verified erasures.</summary>
    public const string Erase = "erase";
    /// <summary>Mounting a pack's local copy for one brain and owner.</summary>
    public const string Mount = "mount";
    /// <summary>Detaching a pack's local copy from one brain and owner.</summary>
    public const string Detach = "detach";
}

/// <summary>The steps inside a pack operation a <see cref="PackFailure"/> names.</summary>
[Experimental("AMEXT001")]
public static class PackSteps
{
    /// <summary>Checks before anything is written: validity window, downgrade, bound digest.</summary>
    public const string Validate = "validate";
    /// <summary>Reading what the store holds of the pack.</summary>
    public const string State = "state";
    /// <summary>Applying the pack's schema (constraints, indexes, search views).</summary>
    public const string Schema = "schema";
    /// <summary>Writing or closing the pack's records.</summary>
    public const string Records = "records";
    /// <summary>Removing erased records and keeping their tombstones.</summary>
    public const string Erasures = "erasures";
    /// <summary>Embedding the pack's records for search by meaning.</summary>
    public const string MeaningIndex = "meaning_index";
    /// <summary>Dropping the vectors of closed or erased records.</summary>
    public const string MeaningForget = "meaning_forget";
    /// <summary>Creating, rebasing or deactivating a brain's local copy of the pack.</summary>
    public const string LocalCopy = "local_copy";
    /// <summary>Inside a store that did not say which step; the exception's own message is the detail.</summary>
    public const string Store = "store";
}

/// <summary>Why a pack operation failed, in one word.</summary>
[Experimental("AMEXT001")]
public static class PackFailureKinds
{
    /// <summary>The library refused the pack or the request (the message says why); retrying the same thing fails the same way.</summary>
    public const string Refused = "refused";
    /// <summary>The database did not answer in time or the connection failed; a retry may work.</summary>
    public const string Unavailable = "unavailable";
    /// <summary>Anything else; the exception is the detail.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// One pack operation's span and log line, for the library's importer and stores and for any other
/// <see cref="IKnowledgePackStore"/>. A failure gets a <see cref="PackFailure"/> attached (unless a deeper step attached one
/// first), an error status and one log line with the same fields.
/// </summary>
[Experimental("AMEXT001")]
public sealed class PackOperationTrace : IDisposable
{
    private readonly Activity? _activity;
    private readonly ILogger? _logger;
    private readonly long _started = Stopwatch.GetTimestamp();

    private PackOperationTrace(string operation, string packId, string? version, string? digest, ILogger? logger)
    {
        Operation = operation;
        PackId = packId;
        Version = version;
        Digest = digest;
        _logger = logger;
        _activity = AgentMemoryDiagnostics.Source.StartActivity(ModuleTelemetry.Pack);
        if (_activity is { IsAllDataRequested: true }) Tag(_activity, operation, packId, version, digest);
    }

    /// <summary>The operation (see <see cref="PackOperations"/>).</summary>
    public string Operation { get; }
    /// <summary>The pack id.</summary>
    public string PackId { get; }
    /// <summary>The pack version, when known.</summary>
    public string? Version { get; }
    /// <summary>The pack's bundle digest, when known.</summary>
    public string? Digest { get; }

    /// <summary>The step running now; a failure without a deeper step is reported at it.</summary>
    public string Step { get; set; } = PackSteps.Store;

    /// <summary>Starts the operation's span; with a logger, its end writes one log line.</summary>
    public static PackOperationTrace Start(string operation, string packId, string? version, string? digest, ILogger? logger) =>
        new(operation, packId, version, digest, logger);

    /// <summary>Marks the operation completed; <paramref name="summary"/> is a short count of what changed, never content.</summary>
    public void Completed(string summary)
    {
        Outcome(_activity, "Completed");
        _logger?.LogInformation("Pack {PackId} {PackVersion}: {PackOperation} completed in {ElapsedMs} ms ({Summary}).",
            PackId, Version, Operation, (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds, summary);
    }

    /// <summary>
    /// Records a failure at the current <see cref="Step"/> (unless a deeper step attached one first); a cancellation the caller asked
    /// for is recorded as cancelled, not as a failure. The caller rethrows.
    /// </summary>
    public void Failed(Exception error, CancellationToken cancellationToken)
    {
        if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            Outcome(_activity, "Cancelled");
            return;
        }
        var failure = PackFailure.Attach(error, Operation, Step, PackId, Version, Digest);
        Fail(_activity, failure);
        if (_logger is not null && PackFailure.FirstLog(error))
            _logger.LogError(error, "Pack {PackId} {PackVersion}: {PackOperation} failed at {PackStep} ({PackFailureCode}) after {ElapsedMs} ms.",
                failure.PackId, failure.Version, failure.Operation, failure.Step, failure.Code, (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds);
    }

    /// <summary>Ends the span.</summary>
    public void Dispose() => _activity?.Dispose();

    /// <summary>
    /// Runs one step of an operation as its own span, and attaches the failure at this step when nothing deeper did. The kind can
    /// be given by a caller that knows its own refusals.
    /// </summary>
    public static async Task<T> StepAsync<T>(string operation, string step, string packId, string? version, string? digest,
        Func<Task<T>> work, CancellationToken cancellationToken, Func<Exception, string?>? kindOf = null)
    {
        using var activity = AgentMemoryDiagnostics.Source.StartActivity(ModuleTelemetry.PackStep);
        if (activity is { IsAllDataRequested: true })
        {
            Tag(activity, operation, packId, version, digest);
            activity.SetTag(ModuleTelemetry.PackStepName, step);
        }
        try
        {
            var result = await work().ConfigureAwait(false);
            Outcome(activity, "Completed");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Outcome(activity, "Cancelled");
            throw;
        }
        catch (Exception error)
        {
            Fail(activity, PackFailure.Attach(error, operation, step, packId, version, digest, kindOf?.Invoke(error)));
            throw;
        }
    }

    /// <summary>Runs one step of an operation as its own span; see the other overload.</summary>
    public static Task StepAsync(string operation, string step, string packId, string? version, string? digest,
        Func<Task> work, CancellationToken cancellationToken, Func<Exception, string?>? kindOf = null) =>
        StepAsync(operation, step, packId, version, digest, async () => { await work().ConfigureAwait(false); return true; }, cancellationToken, kindOf);

    private static void Tag(Activity activity, string operation, string packId, string? version, string? digest)
    {
        activity.SetTag(ModuleTelemetry.PackOperation, operation);
        activity.SetTag(ModuleTelemetry.PackId, packId);
        if (version is not null) activity.SetTag(ModuleTelemetry.PackVersion, version);
        if (digest is not null) activity.SetTag(ModuleTelemetry.PackDigest, digest);
    }

    private static void Fail(Activity? activity, PackFailure failure)
    {
        if (activity is not { IsAllDataRequested: true }) return;
        activity.SetTag(ModuleTelemetry.PackStepName, failure.Step);
        activity.SetTag(ModuleTelemetry.PackFailureCode, failure.Code);
        Outcome(activity, failure.Kind switch
        {
            PackFailureKinds.Refused => "Refused",
            PackFailureKinds.Unavailable => "Unavailable",
            _ => "Failed",
        });
    }

    private static void Outcome(Activity? activity, string outcome)
    {
        if (activity is not { IsAllDataRequested: true }) return;
        activity.SetTag(ModuleTelemetry.Outcome, outcome);
        activity.SetStatus(outcome is "Completed" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
    }
}
