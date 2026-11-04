// SPDX-License-Identifier: Apache-2.0
namespace AgentMemory.Packs.Format;

/// <summary>
/// What a reader holds before it parses content whose signatures it did not check (§1 principle 3, review 7 D2): a tool that
/// reads a pack as it is (<c>ampack diff</c> and <c>scan</c> without a policy) parses nothing larger than verification would,
/// a listed JSON file and a JSONL line at most 1 MiB each, and all of its JSON and JSONL together at most 64 MiB. Verified
/// content passed the first two already; its total is the publisher's, whom the host trusts.
/// </summary>
internal static class PackContentBounds
{
    /// <summary>The most JSON and JSONL bytes a reader parses from a pack it did not verify.</summary>
    internal const long MaximumUnverifiedBytes = 64L << 20;

    /// <summary>Why the listed JSON and JSONL of <paramref name="files"/> may not be parsed, in path order; empty when they may.</summary>
    internal static IReadOnlyList<string> Problems(IReadOnlyDictionary<string, byte[]> files, bool verified)
    {
        var problems = new List<string>();
        long total = 0;
        foreach (var (path, bytes) in files.Where(f => FilesList.IsListed(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (path.EndsWith(".json", StringComparison.Ordinal))
            {
                total += bytes.LongLength;
                if (bytes.Length > PackVerifier.MaximumJsonBytes)
                    problems.Add($"'{path}' is larger than {PackVerifier.MaximumJsonBytes >> 20} MiB: a JSON file in a pack is at most that.");
            }
            else if (path.EndsWith(".jsonl", StringComparison.Ordinal))
            {
                total += bytes.LongLength;
                for (int start = 0, stop; start < bytes.Length; start = stop + 1)
                {
                    stop = Array.IndexOf(bytes, (byte)'\n', start);
                    if (stop < 0) stop = bytes.Length;
                    if (stop - start <= PackVerifier.MaximumJsonBytes) continue;
                    problems.Add($"'{path}' has a line longer than {PackVerifier.MaximumJsonBytes >> 20} MiB.");
                    break;
                }
            }
        }
        if (!verified && total > MaximumUnverifiedBytes)
            problems.Add($"The pack holds {total >> 20} MiB of JSON; a pack read without verifying it is at most {MaximumUnverifiedBytes >> 20} MiB (give --policy to verify it first).");
        return problems;
    }
}
