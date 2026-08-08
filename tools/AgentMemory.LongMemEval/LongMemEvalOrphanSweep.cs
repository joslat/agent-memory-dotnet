using System.Diagnostics;
using System.Globalization;

namespace AgentMemory.LongMemEval;

internal sealed record LongMemEvalVolumeCandidate(string Name, DateTimeOffset CreatedAt);

internal sealed record LongMemEvalOrphanSkip(string Name, string Reason);

internal sealed record LongMemEvalOrphanSweepDecision(
    IReadOnlyList<string> Removable,
    IReadOnlyList<LongMemEvalOrphanSkip> Skipped);

/// <summary>
/// G3B.12-R. Removes prepared volumes left behind by runs that were killed before they could clean
/// up after themselves.
/// </summary>
/// <remarks>
/// Written against a measurement rather than an assumption: the leak was specified as abandoned
/// Neo4j containers holding retained volumes, but <c>docker ps -a</c> was empty and every volume
/// reported zero links, so Testcontainers' reaper had in fact removed every container. The real leak
/// was 25 orphaned volumes holding about 17.2 GB. This therefore sweeps volumes, not containers.
/// <para>
/// Attachment is not checked by listing containers first: that answer can go stale between the check
/// and the delete. The daemon is asked to remove the volume and its own "volume is in use" refusal is
/// reported as a skip, which is atomic. <c>--force</c> is never passed.
/// </para>
/// </remarks>
internal static class LongMemEvalOrphanSweep
{
    /// <summary>The prefix <see cref="LongMemEvalPreparedVolumes"/> gives every volume it creates.</summary>
    internal const string Prefix = "am-lme-";

    /// <summary>
    /// A volume younger than this may belong to a run that is executing right now: clone targets are
    /// created up front and only mounted once a ~22 minute preparation finishes, so for that whole
    /// window a live run owns volumes that nothing is attached to yet.
    /// </summary>
    internal static readonly TimeSpan DefaultMinimumAge = TimeSpan.FromMinutes(120);

    internal static LongMemEvalOrphanSweepDecision Select(
        IReadOnlyList<LongMemEvalVolumeCandidate> candidates,
        string? protectedVolumeName,
        DateTimeOffset now,
        TimeSpan? minimumAge = null,
        IReadOnlyCollection<string>? pinned = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var age = minimumAge ?? DefaultMinimumAge;
        var pins = pinned is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(pinned, StringComparer.Ordinal);

        // Unrelated volumes on a developer machine are not this tool's property to delete, and are
        // not reported either - listing them as skips would bury the real output in noise.
        var ours = candidates
            .Where(candidate => candidate.Name.StartsWith(Prefix, StringComparison.Ordinal))
            .ToArray();

        var newestBase = ours
            .Where(candidate => IsBase(candidate.Name))
            .OrderByDescending(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        var surviving = new HashSet<string>(
            ours.Select(candidate => candidate.Name), StringComparer.Ordinal);

        var removable = new List<string>();
        var skipped = new List<LongMemEvalOrphanSkip>();
        foreach (var candidate in ours)
        {
            if (IsProtected(candidate.Name, protectedVolumeName))
            {
                skipped.Add(new LongMemEvalOrphanSkip(
                    candidate.Name, "named for reuse by this run"));
                continue;
            }

            if (pins.Contains(candidate.Name))
            {
                skipped.Add(new LongMemEvalOrphanSkip(candidate.Name, "pinned by the operator"));
                continue;
            }

            // A clone is only cheap to recreate while the base it was cloned from still exists.
            // Treating "clone" as a synonym for "worthless" destroyed a deliberately retained
            // pre-vocabulary baseline whose base had already been removed - it was the only copy.
            if (BaseNameOf(candidate.Name) is { } baseName && !surviving.Contains(baseName))
            {
                skipped.Add(new LongMemEvalOrphanSkip(
                    candidate.Name,
                    "its base volume is gone, so it cannot be regenerated and is the only copy"));
                continue;
            }

            if (now - candidate.CreatedAt < age)
            {
                skipped.Add(new LongMemEvalOrphanSkip(
                    candidate.Name,
                    $"below the minimum age of {age.TotalMinutes:F0} minutes; a concurrent run may own it"));
                continue;
            }

            if (newestBase is not null &&
                string.Equals(candidate.Name, newestBase.Name, StringComparison.Ordinal))
            {
                skipped.Add(new LongMemEvalOrphanSkip(
                    candidate.Name, "newest cold build; it cost 121 provider calls"));
                continue;
            }

            removable.Add(candidate.Name);
        }

        return new LongMemEvalOrphanSweepDecision(removable, skipped);
    }

    /// <summary>
    /// Lists prepared volumes, removes the ones the policy selects, and reports what it did.
    /// </summary>
    /// <remarks>
    /// Never throws and never fails the run: a housekeeping step must not be able to stop an
    /// evaluation that would otherwise succeed.
    /// </remarks>
    internal static async Task RunAsync(
        string? protectedVolumeName,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            var listing = await DockerAsync(
                    ["volume", "ls", "--format", "{{.Name}}"], cancellationToken)
                .ConfigureAwait(false);
            if (listing.ExitCode != 0)
            {
                log.WriteLine("longmemeval: orphan sweep skipped; could not list Docker volumes.");
                return;
            }

            var names = listing.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
                .ToArray();
            if (names.Length == 0)
                return;

            var candidates = await InspectAsync(names, cancellationToken).ConfigureAwait(false);
            var decision = Select(
                candidates,
                protectedVolumeName,
                DateTimeOffset.UtcNow,
                minimumAge: null,
                pinned: ReadPins());
            if (decision.Removable.Count == 0)
            {
                log.WriteLine(
                    $"longmemeval: orphan sweep found {candidates.Count} prepared volumes and removed none.");
                return;
            }

            var removed = 0;
            foreach (var name in decision.Removable)
            {
                var removal = await DockerAsync(["volume", "rm", name], cancellationToken)
                    .ConfigureAwait(false);
                if (removal.ExitCode == 0)
                {
                    removed++;
                    continue;
                }

                // The daemon's own refusal is the authoritative in-use answer.
                log.WriteLine(
                    $"longmemeval: orphan sweep kept {name}: {Summarize(removal.StandardError)}");
            }

            log.WriteLine(
                $"longmemeval: orphan sweep removed {removed} of {candidates.Count} prepared volumes; " +
                $"kept {candidates.Count - removed}.");
            foreach (var skip in decision.Skipped)
                log.WriteLine($"longmemeval: orphan sweep kept {skip.Name}: {skip.Reason}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.WriteLine($"longmemeval: orphan sweep skipped: {exception.Message}");
        }
    }

    /// <summary>
    /// Volumes the operator has deliberately kept, one name per line, <c>#</c> for comments.
    /// </summary>
    /// <remarks>
    /// An explicit, inspectable pin exists because a document-level note that a volume was "kept
    /// deliberately" is invisible to a sweep, and one was destroyed for exactly that reason.
    /// </remarks>
    internal static string PinFilePath { get; } =
        Path.Combine("artifacts", "evaluation", "pinned-volumes.txt");

    private static IReadOnlyCollection<string> ReadPins()
    {
        if (!File.Exists(PinFilePath))
            return [];
        return File.ReadAllLines(PinFilePath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
    }

    /// <summary>
    /// The base volume a clone was produced from, or null when the name is not a clone.
    /// </summary>
    private static string? BaseNameOf(string name)
    {
        // AdoptAsync names its targets "{base}-reuse-structured-{suffix}".
        var reuse = name.IndexOf("-reuse-", StringComparison.Ordinal);
        if (reuse > 0)
            return name[..reuse];

        foreach (var kind in new[] { "-structured-", "-hybrid-" })
        {
            var index = name.IndexOf(kind, StringComparison.Ordinal);
            if (index > 0)
                return string.Concat(name.AsSpan(0, index), "-base-", name.AsSpan(index + kind.Length));
        }

        return null;
    }

    private static bool IsBase(string name) =>
        name.Contains("-base-", StringComparison.Ordinal);

    private static bool IsProtected(string name, string? protectedVolumeName) =>
        !string.IsNullOrWhiteSpace(protectedVolumeName) &&
        // Clone targets are named after the base they were adopted from, so one prefix test covers
        // the adopted build and the in-flight clones this run just created beside it.
        name.StartsWith(protectedVolumeName, StringComparison.Ordinal);

    private static async Task<IReadOnlyList<LongMemEvalVolumeCandidate>> InspectAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>(names.Count + 3)
        {
            "volume", "inspect", "--format", "{{.Name}}\t{{.CreatedAt}}"
        };
        arguments.AddRange(names);
        var inspection = await DockerAsync(arguments, cancellationToken).ConfigureAwait(false);

        var candidates = new List<LongMemEvalVolumeCandidate>(names.Count);
        foreach (var line in inspection.StandardOutput.Split(
                     '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('\t', StringComparison.Ordinal);
            if (separator <= 0)
                continue;
            var name = line[..separator];
            if (!DateTimeOffset.TryParse(
                    line[(separator + 1)..],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var createdAt))
            {
                // An unparseable creation time means the age guard cannot be evaluated, so the
                // volume is simply not a candidate for removal.
                continue;
            }

            candidates.Add(new LongMemEvalVolumeCandidate(name, createdAt));
        }

        return candidates;
    }

    private static string Summarize(string standardError)
    {
        var first = standardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "removal failed";
        return first.Length > 200 ? first[..200] : first;
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> DockerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Docker CLI.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }
}
