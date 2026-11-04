// SPDX-License-Identifier: Apache-2.0
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// Subscriptions to <c>ampack://status/{id}</c> (feature 17, R1): only a pack's status can be subscribed to, and each check
/// says which subscribed statuses changed since they were last seen. A status changes when a pack comes on or goes off (its
/// validUntil passes, its policy ends) or when what is mounted changes; the server checks on a timer and tells the client.
/// </summary>
[Experimental("AMEXT001")]
public sealed class PackStatusWatch
{
    /// <summary>The one resource that can be subscribed to, before its pack id.</summary>
    public const string Prefix = "ampack://status/";

    /// <summary>The most statuses subscribed at once: each is checked on every tick.</summary>
    public const int MaximumSubscriptions = 256;

    private readonly ConcurrentDictionary<string, string> _seen = new(StringComparer.Ordinal);

    /// <summary>The subscribed resources.</summary>
    public IReadOnlyCollection<string> Subscribed => [.. _seen.Keys];

    /// <summary>Subscribes to a status resource, remembering its status now; false for any other resource, or past the most.</summary>
    /// <param name="uri">The resource.</param>
    /// <param name="status">A pack's status by its id, as the resource answers it.</param>
    public bool Subscribe(string uri, Func<string, string> status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!IsStatus(uri) || _seen.Count >= MaximumSubscriptions && !_seen.ContainsKey(uri)) return false;
        _seen[uri] = status(Id(uri));
        return true;
    }

    /// <summary>Ends a subscription; one that was not there is no error.</summary>
    public void Unsubscribe(string uri) => _seen.TryRemove(uri, out _);

    /// <summary>The subscribed resources whose status differs from the one last seen, each now remembered as seen.</summary>
    public IReadOnlyList<string> Changed(Func<string, string> status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var changed = new List<string>();
        foreach (var (uri, seen) in _seen)
        {
            var now = status(Id(uri));
            if (now == seen || !_seen.TryUpdate(uri, now, seen)) continue;
            changed.Add(uri);
        }
        return changed;
    }

    private static bool IsStatus(string? uri) => uri is not null && uri.StartsWith(Prefix, StringComparison.Ordinal) && uri.Length > Prefix.Length && !uri[Prefix.Length..].Contains('/');

    private static string Id(string uri) => Uri.UnescapeDataString(uri[Prefix.Length..]);
}
