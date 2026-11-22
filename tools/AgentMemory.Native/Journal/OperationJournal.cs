using System.Buffers;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Native.Journal;

/// <summary>Where an explicit write stands. Only <see cref="Applied"/> supports saying "remembered".</summary>
internal enum OperationState
{
    Accepted,
    Running,
    Applied,
    Rejected,
    Partial,
    Unknown,
    Kept,

    /// <summary>Accepted, then cancelled before it ran (a forget or an erase came first): it never wrote anything.</summary>
    Cancelled,

    /// <summary>A remember whose memories a later forget closed: recall no longer returns them (they are not deleted).</summary>
    Forgotten,

    /// <summary>A write of an owner who was erased since: what it wrote is deleted, and its words are gone from the journal.</summary>
    Erased,
}

/// <summary>What an operation does.</summary>
internal enum OperationKind
{
    Remember,
    Forget,
    Erase,
}

/// <summary>A journal that cannot be used: held by another process, damaged, or not writable.</summary>
internal sealed class JournalException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>One line of the journal. Which fields are set depends on <see cref="Kind"/>.</summary>
internal sealed class JournalRecord
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("operationId")] public required string OperationId { get; init; }
    [JsonPropertyName("at")] public required DateTimeOffset At { get; init; }

    // accepted
    [JsonPropertyName("sourceId")] public string? SourceId { get; init; }
    [JsonPropertyName("sourceSha256")] public string? SourceSha256 { get; init; }
    [JsonPropertyName("owner")] public string? Owner { get; init; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("occurredAt")] public DateTimeOffset? OccurredAt { get; init; }
    [JsonPropertyName("op")] public string? Op { get; init; }
    [JsonPropertyName("targetOperationId")] public string? TargetOperationId { get; init; }
    [JsonPropertyName("memoryIds")] public List<string>? MemoryIds { get; init; }
    [JsonPropertyName("origin")] public string? Origin { get; init; }

    // ends
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("facts")] public List<string>? Facts { get; init; }
    [JsonPropertyName("preferences")] public List<string>? Preferences { get; init; }
    [JsonPropertyName("closed")] public List<string>? Closed { get; init; }
    [JsonPropertyName("searchable")] public bool? Searchable { get; init; }
    [JsonPropertyName("count")] public long? Count { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JournalRecord))]
internal sealed partial class JournalJsonContext : JsonSerializerContext
{
}

/// <summary>One explicit write as the journal knows it. Mutated only under the journal's lock.</summary>
internal sealed class Operation
{
    public required string OperationId { get; init; }
    public required string SourceId { get; init; }
    public required string SourceSha256 { get; init; }
    public required string Owner { get; init; }
    public required string SessionId { get; init; }
    public OperationKind Kind { get; init; } = OperationKind.Remember;
    public string? TargetOperationId { get; init; }
    public IReadOnlyList<string> MemoryIds { get; init; } = [];

    /// <summary>A remember's origin: "explicit" (the owner asked) or "observed" (admitted by the learning profile).</summary>
    public string Origin { get; init; } = "explicit";

    /// <summary>How many times the learning profile put this operation back in the queue (crash or store failure).</summary>
    public int Retries { get; set; }

    /// <summary>The remembered words; null for a forget or an erase, and after the owner was erased.</summary>
    public string? Text { get; set; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required DateTimeOffset AcceptedAt { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public OperationState State { get; set; } = OperationState.Accepted;
    public string? Reason { get; set; }
    public IReadOnlyList<string> Facts { get; set; } = [];
    public IReadOnlyList<string> Preferences { get; set; } = [];
    public IReadOnlyList<string> Closed { get; set; } = [];
    public bool Searchable { get; set; }
    public long? Count { get; set; }

    /// <summary>Completes when the operation leaves <see cref="OperationState.Accepted"/> and <see cref="OperationState.Running"/>.</summary>
    public TaskCompletionSource Ended { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Back in the queue (learning profile): whoever waits from now on waits for the next ending.</summary>
    public void ReopenEnded()
    {
        if (Ended.Task.IsCompleted) Ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public bool IsOpen => State is OperationState.Accepted or OperationState.Running or OperationState.Partial or OperationState.Unknown;
    public bool IsPending => State is OperationState.Accepted or OperationState.Running;

    /// <summary>A consistent copy for a receipt, taken under the journal's lock.</summary>
    public OperationSnapshot Snapshot() => new(OperationId, SourceId, State, Reason, OccurredAt, AcceptedAt, StartedAt,
        FinishedAt, Facts, Preferences, Closed, Searchable, Kind, TargetOperationId, Count, Origin);
}

/// <summary>An operation as a receipt shows it.</summary>
internal sealed record OperationSnapshot(
    string OperationId, string SourceId, OperationState State, string? Reason, DateTimeOffset OccurredAt,
    DateTimeOffset AcceptedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, IReadOnlyList<string> Facts,
    IReadOnlyList<string> Preferences, IReadOnlyList<string> Closed, bool Searchable, OperationKind Kind = OperationKind.Remember,
    string? TargetOperationId = null, long? Count = null, string Origin = "explicit");

/// <summary>
/// The durable journal of explicit writes: one JSON Lines file in a state directory held with an exclusive lock for the
/// journal's life. Each line is <c>sha256-hex SPACE json</c>; a line counts once it ends with a newline and its hash
/// matches. An append returns only after the bytes are flushed to the disk, so "accepted" is a durable fact.
/// </summary>
/// <remarks>
/// <para>On open, a damaged or unfinished last line is cut off: its append never returned, so nobody was told about it.
/// A damaged line before the last means the file was changed by something else; the journal refuses to open.</para>
/// <para>Recovery: an operation that was running when the last process ended becomes <see cref="OperationState.Unknown"/>
/// (its effects may or may not have committed; it is never run again). One that was only accepted stays accepted and
/// is run by the writer.</para>
/// </remarks>
internal sealed class OperationJournal : IDisposable
{
    public const string FileName = "journal.jsonl";
    public const string LockName = "agentmemory.lock";

    private readonly object _gate = new();
    private readonly FileStream _lock;
    private readonly string _path;
    private FileStream _file;
    private readonly Dictionary<string, Operation> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Operation> _bySource = new(StringComparer.Ordinal);
    private readonly List<Operation> _order = [];
    private bool _disposed;

    private OperationJournal(FileStream lockStream, FileStream file, string path)
    {
        _lock = lockStream;
        _file = file;
        _path = path;
    }

    /// <summary>The object every change of an operation is made under; the writer and the readers lock it too.</summary>
    public object Gate => _gate;

    public static OperationJournal Open(string directory, string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        FileStream? lockStream = null;
        FileStream? file = null;
        try
        {
            CreatePrivateDirectory(directory);
            try
            {
                lockStream = new FileStream(Path.Combine(directory, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.None);
            }
            catch (IOException exception)
            {
                throw new JournalException("state_locked",
                    "Another context holds this state directory (in this process or another one); close it first.", exception);
            }
            var path = Path.Combine(directory, FileName);
            var created = !File.Exists(path);
            file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.None);
            if (created && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var journal = new OperationJournal(lockStream, file, path);
            journal.Load(owner);
            return journal;
        }
        catch (JournalException)
        {
            file?.Dispose();
            lockStream?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            file?.Dispose();
            lockStream?.Dispose();
            throw new JournalException("journal_unavailable", $"The state directory cannot be used: {exception.Message}", exception);
        }
    }

    private static void CreatePrivateDirectory(string directory)
    {
        if (Directory.Exists(directory)) return;
        if (OperatingSystem.IsWindows()) CreatePrivateDirectoryOnWindows(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// The Windows counterpart of mode 0700: an access list that grants full control to the creating user and to SYSTEM
    /// (the counterpart of root) only, inherits nothing from the parent, and is inherited by every file created inside,
    /// the journal and its lock among them. Missing parents are created as ordinary folders, as on Unix.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void CreatePrivateDirectoryOnWindows(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(full) is { } parent) Directory.CreateDirectory(parent);
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm($"D:P(A;OICI;FA;;;{identity.User!.Value})(A;OICI;FA;;;SY)", AccessControlSections.Access);
        security.CreateDirectory(full);
    }

    private void Load(string owner)
    {
        _file.Position = 0;
        var bytes = new byte[_file.Length];
        _file.ReadExactly(bytes);
        var good = 0;
        var position = 0;
        var records = new List<JournalRecord>();
        while (position < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', position);
            var last = end < 0 || end == bytes.Length - 1;
            var record = end < 0 ? null : Decode(bytes.AsSpan(position, end - position));
            if (record is null)
            {
                if (!last)
                    throw new JournalException("journal_corrupt",
                        $"The journal is damaged at byte {position}, before its last line; it was changed by something else.");
                break;   // an append that never completed: cut off below
            }
            records.Add(record);
            position = end + 1;
            good = position;
        }
        if (good < bytes.Length)
        {
            _file.SetLength(good);
            _file.Flush(flushToDisk: true);
        }
        _file.Position = _file.Length;

        foreach (var record in records) Apply(record, owner);
        // A run the previous process did not finish may have committed some effects: it is never repeated.
        foreach (var operation in _order.Where(o => o.State == OperationState.Running).ToList())
            AppendLocked(new JournalRecord
            {
                Kind = "unknown", OperationId = operation.OperationId, At = DateTimeOffset.UtcNow,
                Reason = "process_ended_while_running",
            });
    }

    private static JournalRecord? Decode(ReadOnlySpan<byte> line)
    {
        if (line.Length < 66 || line[64] != (byte)' ') return null;
        var json = line[65..];
        if (!string.Equals(Encoding.ASCII.GetString(line[..64]), Convert.ToHexStringLower(SHA256.HashData(json)), StringComparison.Ordinal))
            return null;
        try
        {
            return JsonSerializer.Deserialize(json, JournalJsonContext.Default.JournalRecord);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Apply(JournalRecord record, string owner)
    {
        if (record.Kind == "accepted")
        {
            if (record.Owner != owner)
                throw new JournalException("journal_owner_mismatch",
                    "The journal in this state directory belongs to another owner; give each owner its own directory.");
            var operation = new Operation
            {
                OperationId = record.OperationId, SourceId = record.SourceId!, SourceSha256 = record.SourceSha256!,
                Owner = record.Owner!, SessionId = record.SessionId!, Text = record.Text, OccurredAt = record.OccurredAt!.Value,
                AcceptedAt = record.At,
                Kind = record.Op switch
                {
                    null or "remember" => OperationKind.Remember,
                    "forget" => OperationKind.Forget,
                    "erase" => OperationKind.Erase,
                    var other => throw new JournalException("journal_corrupt", $"The journal holds an unknown operation '{other}'."),
                },
                TargetOperationId = record.TargetOperationId,
                MemoryIds = record.MemoryIds ?? [],
                Origin = record.Origin ?? "explicit",
            };
            _byId[operation.OperationId] = operation;
            _bySource[operation.SourceId] = operation;
            _order.Add(operation);
            return;
        }
        if (!_byId.TryGetValue(record.OperationId, out var known))
            throw new JournalException("journal_corrupt", $"The journal names operation '{record.OperationId}' before accepting it.");
        switch (record.Kind)
        {
            case "running":
                known.State = OperationState.Running;
                known.StartedAt = record.At;
                break;
            case "retry":
                // Learning profile: an operation applied once by its id can run again safely (its receipt decides).
                known.State = OperationState.Accepted;
                known.FinishedAt = null;
                known.Reason = record.Reason;
                known.Retries++;
                known.ReopenEnded();
                break;
            case "applied" or "rejected" or "partial" or "unknown" or "kept" or "cancelled" or "forgotten" or "erased":
                known.State = record.Kind switch
                {
                    "applied" => OperationState.Applied,
                    "rejected" => OperationState.Rejected,
                    "partial" => OperationState.Partial,
                    "unknown" => OperationState.Unknown,
                    "kept" => OperationState.Kept,
                    "cancelled" => OperationState.Cancelled,
                    "forgotten" => OperationState.Forgotten,
                    _ => OperationState.Erased,
                };
                known.FinishedAt = record.At;
                // An ending says why it ended, or nothing: a reason from before (a retry's) never outlives it.
                known.Reason = record.Reason;
                if (record.Facts is not null) known.Facts = record.Facts;
                if (record.Preferences is not null) known.Preferences = record.Preferences;
                if (record.Closed is not null) known.Closed = record.Closed;
                if (record.Searchable is not null) known.Searchable = record.Searchable.Value;
                if (record.Count is not null) known.Count = record.Count;
                if (!known.IsPending) known.Ended.TrySetResult();
                break;
            default:
                throw new JournalException("journal_corrupt", $"The journal holds an unknown record kind '{record.Kind}'.");
        }
    }

    /// <summary>Writes one record, flushed to the disk, then applies it. Call under <see cref="Gate"/>.</summary>
    public void AppendLocked(JournalRecord record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var line = Line(record);
        var start = _file.Length;
        try
        {
            _file.Position = start;
            _file.Write(line);
            _file.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not durable: take the partial line back, so the next append starts on a clean line.
            try
            {
                _file.SetLength(start);
                _file.Flush(flushToDisk: true);
            }
            catch (IOException)
            {
                // The next open cuts an unfinished last line off anyway.
            }
            throw new JournalException("journal_unavailable", $"The journal could not be written: {exception.Message}", exception);
        }
        Apply(record, record.Kind == "accepted" ? record.Owner! : _byId[record.OperationId].Owner);
    }

    private static byte[] Line(JournalRecord record)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(record, JournalJsonContext.Default.JournalRecord);
        var line = new ArrayBufferWriter<byte>(json.Length + 66);
        line.Write(Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(json))));
        line.Write(" "u8);
        line.Write(json);
        line.Write("\n"u8);
        return line.WrittenSpan.ToArray();
    }

    /// <summary>The record kind that ends an operation in <paramref name="state"/>.</summary>
    public static string EndKind(OperationState state) => state switch
    {
        OperationState.Applied => "applied",
        OperationState.Rejected => "rejected",
        OperationState.Partial => "partial",
        OperationState.Unknown => "unknown",
        OperationState.Kept => "kept",
        OperationState.Cancelled => "cancelled",
        OperationState.Forgotten => "forgotten",
        OperationState.Erased => "erased",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not an ending."),
    };

    /// <summary>
    /// Owner erase: rewrites the journal from the operations it holds with every remembered text removed. Ids, source
    /// hashes, states and times stay, so a resubmitted old source is still recognised (and never run again). The new
    /// file is written beside the old one, flushed to the disk and renamed over it, so a crash leaves one or the other,
    /// whole. Call under <see cref="Gate"/>.
    /// </summary>
    public void PurgeTextsLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var operation in _order) operation.Text = null;
        var temporary = _path + ".purge";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var operation in _order)
                {
                    stream.Write(Line(new JournalRecord
                    {
                        Kind = "accepted", OperationId = operation.OperationId, At = operation.AcceptedAt,
                        SourceId = operation.SourceId, SourceSha256 = operation.SourceSha256, Owner = operation.Owner,
                        SessionId = operation.SessionId, OccurredAt = operation.OccurredAt,
                        Op = operation.Kind switch { OperationKind.Forget => "forget", OperationKind.Erase => "erase", _ => null },
                        TargetOperationId = operation.TargetOperationId,
                        MemoryIds = operation.MemoryIds.Count == 0 ? null : [.. operation.MemoryIds],
                        Origin = operation.Kind == OperationKind.Remember && operation.Origin != "explicit" ? operation.Origin : null,
                    }));
                    if (operation.StartedAt is { } started)
                        stream.Write(Line(new JournalRecord { Kind = "running", OperationId = operation.OperationId, At = started }));
                    if (!operation.IsPending)
                        stream.Write(Line(new JournalRecord
                        {
                            Kind = EndKind(operation.State), OperationId = operation.OperationId,
                            At = operation.FinishedAt ?? DateTimeOffset.UtcNow, Reason = operation.Reason,
                            Facts = [.. operation.Facts], Preferences = [.. operation.Preferences], Closed = [.. operation.Closed],
                            Searchable = operation.Searchable, Count = operation.Count,
                        }));
                }
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _file.Dispose();
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new JournalException("journal_unavailable", $"The journal could not be rewritten: {exception.Message}", exception);
        }
        finally
        {
            // Whichever file now stands at the journal's path (the rewritten one, or the old one if the rename failed).
            if (!_file.CanWrite)
            {
                _file = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.None);
                _file.Position = _file.Length;
            }
        }
    }

    public Operation? FindLocked(string operationId) => _byId.GetValueOrDefault(operationId);

    public Operation? FindBySourceLocked(string sourceId) => _bySource.GetValueOrDefault(sourceId);

    /// <summary>Operations in acceptance order. Call under <see cref="Gate"/>.</summary>
    public IReadOnlyList<Operation> OperationsLocked => _order;

    /// <summary>The source's identity: owner, session, source id, text and spoken time, hashed in one canonical form.</summary>
    public static string SourceHash(string owner, string sessionId, string sourceId, string text, DateTimeOffset occurredAt)
    {
        var buffer = new ArrayBufferWriter<byte>(256 + text.Length * 3);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("agentmemory.source.v1");
            writer.WriteStringValue(owner);
            writer.WriteStringValue(sessionId);
            writer.WriteStringValue(sourceId);
            writer.WriteStringValue(text);
            writer.WriteStringValue(occurredAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _file.Dispose();
        _lock.Dispose();
    }
}
