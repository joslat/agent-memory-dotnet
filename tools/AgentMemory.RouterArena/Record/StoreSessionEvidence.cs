using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.RouterArena.Record;

/// <summary>Opt-in evidence for a recorder run. Hashes identify files; revision metadata is not a clean-build attestation.</summary>
internal sealed class StoreSessionEvidence
{
    private readonly Dictionary<string, string> _inputPaths;
    private readonly Dictionary<string, string> _inputs;
    private readonly List<object> _events = [];
    private readonly Dictionary<string, (Message Message, string? OwnerId)> _messages = new(StringComparer.Ordinal);
    private readonly object[] _binaries;
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    internal StoreSessionEvidence(string packPath, string setsDirectory, string set, string binaryDirectory)
    {
        _inputPaths = new Dictionary<string, string>
        {
            ["pack"] = packPath,
            ["stores"] = Path.Combine(setsDirectory, $"{set}-stores.json"),
            ["none"] = Path.Combine(setsDirectory, $"{set}-none.json"),
        };
        _inputs = _inputPaths.ToDictionary(x => x.Key, x => HashFile(x.Value));
        _binaries = Directory.EnumerateFiles(binaryDirectory, "*.dll")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(path => (object)new
            {
                file = Path.GetFileName(path), sha256 = HashFile(path),
                informationalVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion,
            }).ToArray();
    }

    internal void VerifyInputs()
    {
        foreach (var (name, path) in _inputPaths)
            if (_inputs[name] != HashFile(path))
                throw new InvalidOperationException($"Run input changed after fingerprinting: {name}.");
    }

    internal void AddMessage(Message message, string origin, string? ownerId)
    {
        if (_messages.TryGetValue(message.MessageId, out var previous))
        {
            if (previous.Message.SessionId != message.SessionId || previous.Message.TimestampUtc != message.TimestampUtc
                || previous.Message.Role != message.Role || previous.Message.Content != message.Content || previous.OwnerId != ownerId)
                throw new InvalidOperationException("A persisted message ID has conflicting provenance.");
            return;
        }
        _messages.Add(message.MessageId, (message, ownerId));
        _events.Add(new
        {
            id = message.MessageId, session = message.SessionId, recordedAt = message.TimestampUtc,
            role = message.Role, text = message.Content, origin, ownerId,
        });
    }

    internal object Export(string ownerId, object options, bool complete) => new
    {
        schemaVersion = 1, runId = _runId, startedAtUtc = _startedAt, complete, ownerId,
        isolation = "fresh-container", snapshotScope = "live-semantic-store", versionTime = "observed-at-capture",
        inputs = _inputs, binaries = _binaries, options, events = _events,
    };

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static object? Property(object? value) => value switch
    {
        null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal => value,
        IEnumerable values => values.Cast<object?>().Select(Property).ToArray(),
        // Neo4j temporal values retain their lossless ISO representation instead of driver implementation fields.
        _ => value.ToString(),
    };
}
