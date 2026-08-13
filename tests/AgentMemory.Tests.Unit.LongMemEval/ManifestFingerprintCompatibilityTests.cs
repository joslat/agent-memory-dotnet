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
    public void ATamperedManifestIsStillRejected()
    {
        // The exemption is by ID, so it cannot over-apply. A manifest that is not on the list and does
        // not reproduce is a manifest whose contents no longer match its seal, and that stays fatal --
        // otherwise this change would have removed the guard rather than scoped it.
        var act = () => Verify(WithFingerprint(Manifest(), "0000000000000000"));

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*fingerprint mismatch*");
    }

    [Fact]
    public void TheRealSealedCorpusVerifies()
    {
        // Not a synthetic reproduction -- the ACTUAL manifest read out of the pinned 616-call corpus
        // volume. Synthetic fixtures reproduce the bug you already understand; this one reproduces
        // the bug you have. It is the artifact every cheap experiment in this phase reuses, and it
        // could not be opened at all.
        var path = Path.Combine(AppContext.BaseDirectory, "manifest-sealed-20260812T140253Z.json");
        File.Exists(path).Should().BeTrue("the sealed-corpus fixture must ship with the tests");

        var type = typeof(LongMemEvalOracleComparison).Assembly
            .GetType("AgentMemory.LongMemEval.LongMemEvalPreparationManifest")!;
        var options = (System.Text.Json.JsonSerializerOptions)type
            .GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        var manifest = System.Text.Json.JsonSerializer.Deserialize(
            File.ReadAllText(path), type, options)!;

        var act = () => Verify(manifest);

        act.Should().NotThrow("a 616-call corpus must remain OPENABLE by a later build");

        // Openable, and honest about it: the hash does not reproduce, because the field set changed
        // under it. That is recorded, never silently treated as verified.
        type.GetProperty("FingerprintVerified", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manifest).Should().Be(false,
                "this corpus was sealed before 6.5 changed the graph-snapshot shape");
    }

}
