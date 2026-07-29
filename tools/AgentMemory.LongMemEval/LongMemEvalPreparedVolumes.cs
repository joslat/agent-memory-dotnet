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

    private LongMemEvalPreparedVolumes(
        string baseVolumeName,
        IVolume baseVolume,
        string structuredVolumeName,
        IVolume structuredVolume,
        string hybridVolumeName,
        IVolume hybridVolume)
    {
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

    internal static async Task<LongMemEvalPreparedVolumes> CreateAsync(
        string preparationId,
        CancellationToken cancellationToken)
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
        var baseVolume = Build(baseName);
        var structuredVolume = Build(structuredName);
        var hybridVolume = Build(hybridName);
        var volumes = new LongMemEvalPreparedVolumes(
            baseName,
            baseVolume,
            structuredName,
            structuredVolume,
            hybridName,
            hybridVolume);
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
        List<Exception>? failures = null;
        foreach (var volume in new[] { _hybridVolume, _structuredVolume, _baseVolume })
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

    private static IVolume Build(string name) =>
        new VolumeBuilder()
            .WithName(name)
            .WithCleanUp(true)
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
