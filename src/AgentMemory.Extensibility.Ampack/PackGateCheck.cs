using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// What a host's trial gate does with packs, from its signed decisions alone: which go on and which stay off, and why. The
/// same rules a module set applies when it composes with <see cref="ModuleSetBuilder.RequireTrials"/> (and, where given,
/// <see cref="ModuleSetBuilder.RequireTrialsFor"/> and <see cref="ModuleSetBuilder.TrustCertificatesFor"/>), without a store and
/// without running a trial: for an operator, or <c>ampack gate</c>, to see the effect of a decision or a certificate before a
/// host starts.
/// </summary>
/// <param name="On">The packs that go on, by id.</param>
/// <param name="Off">The packs that stay off, with the reason.</param>
[Experimental("AMEXT001")]
public sealed record PackGateCheck(IReadOnlyList<string> On, IReadOnlyList<SwitchedOffModule> Off)
{
    /// <summary>Applies the host's decisions to <paramref name="packs"/> as its module set would.</summary>
    /// <param name="packs">The loaded packs, as the host composes them.</param>
    /// <param name="activations">The host's signed decisions.</param>
    /// <param name="requireTrialsFor">Namespaces where a pack that declares no trial stays off.</param>
    /// <param name="trustCertificatesFor">Namespaces where a decision the host made from an evaluation certificate counts.</param>
    public static PackGateCheck Of(IReadOnlyList<LoadedPack> packs, IPackActivations activations, IReadOnlyList<string>? requireTrialsFor = null,
        IReadOnlyList<string>? trustCertificatesFor = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(activations);
        var builder = new ModuleSetBuilder();
        foreach (var pack in packs) builder.AddPack(pack);
        if (requireTrialsFor is { Count: > 0 }) builder.RequireTrialsFor([.. requireTrialsFor]);
        if (trustCertificatesFor is { Count: > 0 }) builder.TrustCertificatesFor([.. trustCertificatesFor]);
        var (on, off) = PackTrialGate.Apply(builder.Modules, activations, builder.MandatoryTrials, builder.CertificateTrust);
        return new PackGateCheck([.. on.Select(r => r.Manifest.Id)], off);
    }
}
