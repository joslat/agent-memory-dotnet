using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Volumes;

namespace AgentMemory.LongMemEval;

internal sealed record LongMemEvalVolumeCloneTimings(
    double StructuredMilliseconds,
    double HybridMilliseconds);

internal sealed class LongMemEvalPreparedVolumes : IAsyncDisposable
{
    private const string Image = "neo4j:5.26";
    private readonly IVolume _baseVolume;
    private readonly IVolume _structuredVolume;
    private readonly IVolume _hybridVolume;
    private readonly LongMemEvalPreparedVolumeLifecycle _lifecycle = new();
    private readonly bool _retain;
    private readonly bool _adoptedBase;

    private LongMemEvalPreparedVolumes(
        string baseVolumeName,
        IVolume baseVolume,
        string structuredVolumeName,
        IVolume structuredVolume,
        string hybridVolumeName,
        IVolume hybridVolume,
        bool retain,
        bool adoptedBase = false)
    {
        _retain = retain;
        _adoptedBase = adoptedBase;
        BaseVolumeName = baseVolumeName;
        _baseVolume = baseVolume;
        StructuredVolumeName = structuredVolumeName;
        _structuredVolume = structuredVolume;
        HybridVolumeName = hybridVolumeName;
        _hybridVolume = hybridVolume;
    }

    internal string BaseVolumeName { get; }

    internal string StructuredVolumeName { get; }

    internal string HybridVolumeName { get; }

    /// <summary>
    /// Adopts an existing retained base volume and creates fresh clone targets beside it.
    /// </summary>
    /// <remarks>
    /// G3B.12-R. A cold build costs 121 provider calls and ~22 minutes; a killed run forfeits all of
    /// it, and because extraction is non-deterministic (575 → 650 → 700 facts across builds of one
    /// frozen plan) two runs are never a controlled comparison. Adopting a retained build makes a
    /// retrieval-only change - which alters no stored fact - cost only its evaluation.
    /// <para>
    /// The base volume is never disposed here regardless of <paramref name="retain"/>: this object
    /// did not create it, and destroying an input a caller supplied would be a surprising side
    /// effect. Clone targets follow the usual retention rule.
    /// </para>
    /// </remarks>
    internal static async Task<LongMemEvalPreparedVolumes> AdoptAsync(
        string baseVolumeName,
        CancellationToken cancellationToken,
        bool retain = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseVolumeName);
        var suffix = Guid.NewGuid().ToString("N");
        var structuredName = $"{baseVolumeName}-reuse-structured-{suffix}";
        var hybridName = $"{baseVolumeName}-reuse-hybrid-{suffix}";

        // Referenced, not created: WithCleanUp(false) so disposal can never delete the adopted build.
        var baseVolume = new VolumeBuilder().WithName(baseVolumeName).WithCleanUp(false).Build();
        var structuredVolume = Build(structuredName, retain);
        var hybridVolume = Build(hybridName, retain);
        var volumes = new LongMemEvalPreparedVolumes(
            baseVolumeName, baseVolume, structuredName, structuredVolume, hybridName, hybridVolume,
            retain, adoptedBase: true);
        try
        {
            await structuredVolume.CreateAsync(cancellationToken).ConfigureAwait(false);
            await hybridVolume.CreateAsync(cancellationToken).ConfigureAwait(false);
            return volumes;
        }
        catch
        {
            await volumes.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task<LongMemEvalPreparedVolumes> CreateAsync(
        string preparationId,
        CancellationToken cancellationToken,
        bool retain = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preparationId);
        var suffix = Guid.NewGuid().ToString("N");
        var prefix = string.Concat(preparationId.Select(character =>
            char.IsLetterOrDigit(character) || character == '-'
                ? char.ToLowerInvariant(character)
                : '-'));
        if (prefix.Length > 32)
            prefix = prefix[..32];

        var baseName = $"am-lme-{prefix}-base-{suffix}";
        var structuredName = $"am-lme-{prefix}-structured-{suffix}";
        var hybridName = $"am-lme-{prefix}-hybrid-{suffix}";
        var baseVolume = Build(baseName, retain);
        var structuredVolume = Build(structuredName, retain);
        var hybridVolume = Build(hybridName, retain);
        var volumes = new LongMemEvalPreparedVolumes(
            baseName,
            baseVolume,
            structuredName,
            structuredVolume,
            hybridName,
            hybridVolume,
            retain);
        try
        {
            await baseVolume.CreateAsync(cancellationToken).ConfigureAwait(false);
            await structuredVolume.CreateAsync(cancellationToken).ConfigureAwait(false);
            await hybridVolume.CreateAsync(cancellationToken).ConfigureAwait(false);
            return volumes;
        }
        catch
        {
            await volumes.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal string BeginBasePreparation()
    {
        _lifecycle.BeginBasePreparation();
        return BaseVolumeName;
    }

    internal void MarkBaseContainerStopped() => _lifecycle.MarkBaseContainerStopped();

    internal async Task<LongMemEvalVolumeCloneTimings> CloneFrozenBaseAsync(
        CancellationToken cancellationToken)
    {
        _lifecycle.BeginClone();
        try
        {
            var structured = Stopwatch.StartNew();
            await CloneAsync(
                BaseVolumeName,
                _structuredVolume,
                cancellationToken).ConfigureAwait(false);
            structured.Stop();

            var hybrid = Stopwatch.StartNew();
            await CloneAsync(
                BaseVolumeName,
                _hybridVolume,
                cancellationToken).ConfigureAwait(false);
            hybrid.Stop();
            _lifecycle.CompleteClone();
            return new LongMemEvalVolumeCloneTimings(
                structured.Elapsed.TotalMilliseconds,
                hybrid.Elapsed.TotalMilliseconds);
        }
        catch
        {
            _lifecycle.FailClone();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifecycle.Dispose();
        if (_retain)
        {
            // Deliberate: the volumes outlive the run so the graph can be inspected and re-attached.
            // Cleanup becomes the operator's responsibility and the names are printed for that.
            return;
        }

        List<Exception>? failures = null;
        // An adopted base belongs to the caller and is never destroyed by this object.
        var disposable = _adoptedBase
            ? new[] { _hybridVolume, _structuredVolume }
            : new[] { _hybridVolume, _structuredVolume, _baseVolume };
        foreach (var volume in disposable)
        {
            try
            {
                await volume.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
            throw new AggregateException("Failed to dispose LongMemEval volumes.", failures);
    }

    /// <summary>
    /// G3B.6. <paramref name="retain"/> keeps the cold build on disk after the run so a failure can
    /// be analysed against the exact graph that produced it, instead of paying for another
    /// non-deterministic 121-call rebuild that would not reproduce it anyway.
    /// </summary>
    private static IVolume Build(string name, bool retain) =>
        new VolumeBuilder()
            .WithName(name)
            .WithCleanUp(!retain)
            .Build();

    private static async Task CloneAsync(
        string sourceVolumeName,
        IVolume targetVolume,
        CancellationToken cancellationToken)
    {
        await using var helper = new ContainerBuilder(Image)
            .WithEntrypoint("tail")
            .WithCommand("-f", "/dev/null")
            .WithVolumeMount(sourceVolumeName, "/source", AccessMode.ReadOnly)
            .WithVolumeMount(targetVolume, "/target")
            .Build();
        await helper.StartAsync(cancellationToken).ConfigureAwait(false);
        var result = await helper.ExecAsync(
            ["/bin/sh", "-c", "cp -a /source/. /target/"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to clone frozen LongMemEval volume: {result.Stderr}");
        }
    }
}

internal enum LongMemEvalPreparedVolumeState
{
    Created,
    BaseMounted,
    Frozen,
    Cloning,
    Ready,
    Disposed
}

internal sealed class LongMemEvalPreparedVolumeLifecycle
{
    internal LongMemEvalPreparedVolumeState State { get; private set; } =
        LongMemEvalPreparedVolumeState.Created;

    internal void BeginBasePreparation()
    {
        Require(LongMemEvalPreparedVolumeState.Created);
        State = LongMemEvalPreparedVolumeState.BaseMounted;
    }

    internal void MarkBaseContainerStopped()
    {
        Require(LongMemEvalPreparedVolumeState.BaseMounted);
        State = LongMemEvalPreparedVolumeState.Frozen;
    }

    internal void BeginClone()
    {
        Require(LongMemEvalPreparedVolumeState.Frozen);
        State = LongMemEvalPreparedVolumeState.Cloning;
    }

    internal void CompleteClone()
    {
        Require(LongMemEvalPreparedVolumeState.Cloning);
        State = LongMemEvalPreparedVolumeState.Ready;
    }

    internal void FailClone()
    {
        Require(LongMemEvalPreparedVolumeState.Cloning);
        State = LongMemEvalPreparedVolumeState.Frozen;
    }

    internal void Dispose() => State = LongMemEvalPreparedVolumeState.Disposed;

    private void Require(LongMemEvalPreparedVolumeState required)
    {
        if (State != required)
        {
            throw new InvalidOperationException(
                $"LongMemEval volume lifecycle is {State}; expected {required}.");
        }
    }
}
