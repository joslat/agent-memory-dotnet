using System.Reflection;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// A sealed corpus must stay verifiable by later builds (22.4 blocker).
/// </summary>
/// <remarks>
/// <para>
/// The schema-6 fingerprint field set gained <c>AbstentionPolicy</c> and
/// <c>RefusedSourceSessions</c> on 2026-08-12 at 23:37 <b>without a version bump</b> — nine hours
/// after a 616-call corpus had been sealed under the earlier set. That corpus could never verify
/// again: its stored hash covers fewer fields than the recompute, so <c>VerifyIntegrity</c> threw
/// "fingerprint mismatch", which reads as tampering rather than as a versioning mistake here.
/// </para>
/// <para>
/// It surfaced by blocking a paid run, not by failing a test, because nothing pinned the property
/// that actually matters: <b>a manifest this codebase sealed must remain verifiable by the codebase
/// that sealed it and by every later one</b>. That property is what these tests hold.
/// </para>
/// </remarks>
public sealed class ManifestFingerprintCompatibilityTests
{
    private static object Manifest(string abstentionPolicy = "AsSampled")
    {
        var type = typeof(LongMemEvalOracleComparison).Assembly
            .GetType("AgentMemory.LongMemEval.LongMemEvalPreparationManifest")!;
        var create = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!;
        var parameters = create.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            args[i] = p.Name switch
            {
                "preparationId" => "prep-1",
                "datasetSha256" => "sha",
                "agentEvalRevision" => "rev",
                "scopeRunId" => "scope",
                "answerModelId" or "judgeModelId" or "extractionModelId" or "embeddingModelId" => "m",
                "extractionSourceTime" => "metadata-only",
                "abstentionPolicy" => abstentionPolicy,
                // Guarded parameters need plausible values; a zero trips the argument check before
                // the fingerprint under test is ever computed.
                "embeddingDimensions" => 1536,
                "maxRelevantMessages" => 30,
                "initialExtractionCalls" => 1L,
                "questions" => Questions(),
                _ => p.HasDefaultValue ? p.DefaultValue : Default(p.ParameterType),
            };
        }

        return create.Invoke(null, args)!;
    }

    /// <summary>One prepared question — the manifest refuses an empty set, correctly.</summary>
    private static object Questions()
    {
        var assembly = typeof(LongMemEvalOracleComparison).Assembly;
        var questionType = assembly.GetType("AgentMemory.LongMemEval.LongMemEvalPreparedQuestion")!;
        var snapshotType = assembly.GetType("AgentMemory.LongMemEval.LongMemEvalGraphSnapshot")!;
        var snapshot = Activator.CreateInstance(
            snapshotType,
            snapshotType.GetConstructors()[0].GetParameters()
                .Select(p => p.HasDefaultValue ? p.DefaultValue : Default(p.ParameterType))
                .ToArray())!;

        var question = Activator.CreateInstance(
            questionType, 1, "q1", "history-sha", "scope-sha", 10, 2, 1, snapshot)!;

        var list = (System.Collections.IList)Activator.CreateInstance(
            typeof(List<>).MakeGenericType(questionType))!;
        list.Add(question);
        return list;
    }

    private static object? Default(Type type) =>
        type == typeof(string) ? string.Empty
        : type.IsValueType ? Activator.CreateInstance(type)
        : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
            ? Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetGenericArguments()[0]))
            : null;

    private static void Verify(object manifest) =>
        manifest.GetType()
            .GetMethod("VerifyIntegrity", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(manifest, null);

    private static object WithFingerprint(object manifest, string fingerprint)
    {
        var type = manifest.GetType();
        var clone = type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance)
            ?.Invoke(manifest, null) ?? manifest;
        type.GetProperty("Fingerprint")!.SetValue(clone, fingerprint);
        return clone;
    }

    [Fact]
    public void AFreshlySealedManifestVerifies()
    {
        // The baseline property. If this ever fails, sealing and verification have diverged and every
        // corpus produced from that moment is unusable.
        var act = () => Verify(Manifest());

        act.Should().NotThrow();
    }

    [Fact]
    public void AManifestSealedBeforeAbstentionJoinedTheFingerprintStillVerifies()
    {
        // THE regression. Reproduces a corpus sealed under the earlier schema-6 field set by hashing
        // it with that set and asserting the current build still accepts it. Red before the fix: the
        // pinned 616-call corpus threw "fingerprint mismatch" and could not be reused at all.
        var type = typeof(LongMemEvalOracleComparison).Assembly
            .GetType("AgentMemory.LongMemEval.LongMemEvalPreparationManifest")!;
        var legacy = type.GetMethod(
            "ComputeSchema6PreAbstentionFingerprint", BindingFlags.NonPublic | BindingFlags.Static)!;

        var manifest = Manifest();
        var sealedUnderOldRules = WithFingerprint(manifest, (string)legacy.Invoke(null, [manifest])!);

        var act = () => Verify(sealedUnderOldRules);

        act.Should().NotThrow(
            "a corpus this codebase sealed must stay verifiable by the codebase that sealed it");
    }

    [Fact]
    public void AGenuinelyWrongFingerprintIsStillRejected()
    {
        // Accepting two field sets must not become accepting anything. The guard's whole purpose is
        // to refuse a manifest whose contents do not match its seal, and widening it for a versioning
        // mistake must not blunt that.
        var act = () => Verify(WithFingerprint(Manifest(), "0000000000000000"));

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*fingerprint mismatch*");
    }

    [Fact]
    public void TheTwoFieldSetsDisagreeWhenAbstentionIsSet()
    {
        // Proves the compatibility path is doing real work rather than passing trivially: with a
        // non-default AbstentionPolicy the two hashes MUST differ, or the fix would be indistinguish-
        // able from having changed nothing.
        var type = typeof(LongMemEvalOracleComparison).Assembly
            .GetType("AgentMemory.LongMemEval.LongMemEvalPreparationManifest")!;
        var current = type.GetMethod("ComputeFingerprint", BindingFlags.NonPublic | BindingFlags.Static)!;
        var legacy = type.GetMethod(
            "ComputeSchema6PreAbstentionFingerprint", BindingFlags.NonPublic | BindingFlags.Static)!;

        var manifest = Manifest(abstentionPolicy: "TargetProportion");

        ((string)legacy.Invoke(null, [manifest])!)
            .Should().NotBe((string)current.Invoke(null, [manifest])!);
    }
}
