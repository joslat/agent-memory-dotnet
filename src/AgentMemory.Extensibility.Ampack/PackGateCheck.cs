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
/// <param name="OnCertificate">Of the packs that go on, those on a decision the host made from a certificate, with what it rests on.</param>
[Experimental("AMEXT001")]
public sealed record PackGateCheck(IReadOnlyList<string> On, IReadOnlyList<SwitchedOffModule> Off, IReadOnlyDictionary<string, PackDecisionBasis> OnCertificate)
{
    /// <summary>Applies the host's decisions to <paramref name="packs"/> as its module set would.</summary>
    /// <param name="packs">The loaded packs, as the host composes them.</param>
    /// <param name="activations">The host's signed decisions.</param>
    /// <param name="requireTrialsFor">Namespaces where a pack that declares no trial stays off.</param>
    /// <param name="trustCertificatesFor">Namespaces where a decision the host made from an evaluation certificate counts.</param>
    /// <param name="familyMembers">
    /// The ids of packs that are members of a family (a composite in a set): a host composes those with AddFamily, where only
    /// its own trials count for them, never a certificate, and so does this check (review 2: serve counted certificates for
    /// them and served what a host keeps off).
    /// </param>
    /// <param name="floor">The least a trial must show before it counts (<see cref="ModuleSetBuilder.RequireTrialFloor"/>); this
    /// check applies it as the module set does (review 7, G6: it was left out here, so the tools showed packs the host keeps off).</param>
    public static PackGateCheck Of(IReadOnlyList<LoadedPack> packs, IPackActivations activations, IReadOnlyList<string>? requireTrialsFor = null,
        IReadOnlyList<string>? trustCertificatesFor = null, IReadOnlySet<string>? familyMembers = null, PackTrialFloor? floor = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(activations);
        var builder = new ModuleSetBuilder();
        foreach (var pack in packs)
        {
            builder.AddPack(pack);
            if (familyMembers?.Contains(pack.Id) == true && builder.Modules.LastOrDefault(r => r.Manifest.Id == pack.Id) is { } member)
                member.TrustedCertificates = null;
        }
        if (requireTrialsFor is { Count: > 0 }) builder.RequireTrialsFor([.. requireTrialsFor]);
        if (trustCertificatesFor is { Count: > 0 }) builder.TrustCertificatesFor([.. trustCertificatesFor]);
        if (floor is not null) builder.RequireTrialFloor(floor);
        // The host's whole policy, as the module set takes it (review 7, G6: the floor was not passed here).
        var (on, off) = PackTrialGate.Apply(builder.Modules, activations, PackTrialPolicy.Of(builder));
        // The decision that switched each one on, by the gate's own rule: a certificate's only where it counted.
        var certified = new Dictionary<string, PackDecisionBasis>(StringComparer.Ordinal);
        foreach (var registration in on.Where(r => r.Manifest.Trial is not null))
            if (PackTrialGate.Counting(PackTrialGate.Of(activations, registration), builder.CertificateTrust.Any(p => ModuleSetBuilder.Covers(p, registration.Manifest.Id)))
                    ?.Basis is { } basis)
                certified[registration.Manifest.Id] = basis;
        return new PackGateCheck([.. on.Select(r => r.Manifest.Id)], off, certified);
    }
}
