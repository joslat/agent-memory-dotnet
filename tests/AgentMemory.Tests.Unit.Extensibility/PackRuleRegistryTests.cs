using System.Reflection;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The rule registry (review 7's root-cause analysis, the end root: the loop had no registry of rules, doors and guards, so every
/// fix was local by construction). Each rule ampack states has an id, the one place it lives (a symbol, checked to exist), the
/// doors that must reach it (the tool's commands, checked against the door matrix) and the guards that fail when it breaks (test
/// methods and kit vectors, checked to exist). A rule without a working guard is open, with the findings that keep it open, and
/// this test fails if a rule claims a guard it does not have, a door is bound to no rule, or the open list drifts from the rows.
/// </summary>
public sealed class PackRuleRegistryTests
{
    private sealed record Rule(string Id, string Statement, string[] Definitions, string[] Doors, string[] Guards, string? OpenFor = null);

    private static readonly string[] PackDoors =
        ["lint", "build", "sign", "verify", "inspect", "diff", "lock", "migrate", "set", "test", "scan", "countersign", "serve", "install",
         "receipt", "certify", "certificate", "admit", "gate"];

    private static readonly string[] ReadPacks =
        ["verify", "inspect", "diff", "lock", "set", "test", "scan", "countersign", "serve", "install", "receipt", "certify", "certificate", "admit", "gate"];

    private static readonly Rule[] Rules =
    [
        new("ARCHIVE-BOUNDED", "An archive is read under the format's limits: entries, sizes, ratio, names, before anything inflates.",
            ["AgentMemory.Packs.Format.StrictZip.Read"], ReadPacks,
            ["PackFormat:StrictZipTests.A_bomb_is_refused_from_its_declared_ratio_before_inflating", "PackDoorMatrixTests.Every_command_meets_every_hostile_pack_with_a_refusal_or_an_answer_that_is_bounded_and_escaped"]),
        new("FILE-BOUNDED", "Pack code reads a file only through the one bounded reader, or at a listed site with its reason.",
            ["AgentMemory.Packs.Format.BoundedFile.Read"], ["lint", "build", "sign", "migrate", "lock"],
            ["ChokePointRatchetTests.Pack_code_reads_parses_and_prints_only_through_its_choke_points_or_a_listed_site"]),
        new("AUTHENTICATE-FIRST", "Nothing in a pack is parsed beyond its list and envelopes before its signatures hold, at every load path.",
            ["AgentMemory.Packs.Format.PackVerifier.Verify", "AgentMemory.Extensibility.Ampack.PackLoader.Load"],
            ["verify", "test", "serve", "install", "receipt", "certify", "certificate", "admit", "gate"],
            ["AuthenticateFirstTests.H1_every_load_path_refuses_an_unsigned_pack_before_it_reads_pack_json", "ConformanceKitTests.Both_verifiers_reach_every_vector_s_expected_outcome_and_agree_on_seeded_mutants"]),
        new("UNVERIFIED-BOUNDED", "Content read without verifying it is bounded before it is parsed: 1 MiB a file or line, 64 MiB in all.",
            ["AgentMemory.Packs.Format.PackContentBounds.Problems"], ["diff", "scan", "inspect"],
            ["Review7GuardTests.H7_content_read_without_verifying_it_is_bounded_before_it_is_parsed", "PackDoorMatrixTests.Every_command_meets_every_hostile_pack_with_a_refusal_or_an_answer_that_is_bounded_and_escaped"]),
        new("ONE-WRITER-SIGN", "Every signature passes the writer's rules: a writer signs only the minor it writes.",
            ["AgentMemory.Packs.Format.PackBuilder.Sign"], ["build", "sign", "countersign", "set", "certify"],
            ["Review7GuardTests.M6_every_signature_holds_the_writer_s_rules_not_build_alone", "PackFormat:ReadableMinorTests.A_writer_signs_only_the_minor_it_writes"]),
        new("ONE-WRITER-ARCHIVE", "Every archive a writer returns is read back under the reader's own limits and checks first.",
            ["AgentMemory.Packs.Format.PackBuilder.Pack"], ["build", "countersign", "set", "certify"],
            ["Review7GuardTests.M7_the_one_writer_never_writes_an_archive_a_reader_refuses"]),
        new("OUTPUT-ESCAPED", "Every line the tool prints leaves control and bidi characters as escapes.",
            ["tools/Ampack/ShownWriter.cs"], PackDoors,
            ["PackDoorMatrixTests.Every_command_meets_every_hostile_pack_with_a_refusal_or_an_answer_that_is_bounded_and_escaped"]),
        new("VERIFIERS-AGREE", "The two verifiers reach one verdict on every input either reads.",
            ["AgentMemory.Packs.Format.PackVerifier.Verify", "conformance/pack-v1.1/python/ampack_verify.py"], ["verify"],
            ["ConformanceKitTests.Both_verifiers_reach_every_vector_s_expected_outcome_and_agree_on_seeded_mutants"]),
        new("CERTIFICATE-COPIES", "A statement carried in several envelopes is trusted when any copy verifies; additions never take trust away.",
            ["AgentMemory.Extensibility.Ampack.PackCertificates.Read"], ["inspect", "certificate", "admit", "test", "serve"],
            ["PackCertificateTests.Review7_H5_a_broken_copy_of_a_trusted_certificate_never_hides_it_whatever_its_name"]),
        new("EVIDENCE-INTAKE", "Every verdict, made or rechecked, passes one intake: same tasks, consistent arms, one spacing within the window.",
            ["AgentMemory.Extensibility.Modules.PackTrialGate.Intake", "AgentMemory.Extensibility.Modules.PackTrialGate.Recheck"], ["gate", "certify", "admit", "serve", "test"],
            ["Review7GuardTests.L11_every_arm_of_a_trial_was_drawn_at_one_spacing_within_the_history_window", "Review7GuardTests.L16_a_recheck_runs_the_intake_the_decision_ran",
             "kit:certificate-arms-spaced-differently", "kit:certificate-spacing-past-window"]),
        new("CAPS-DECLARED", "The caps every bound rests on are the declarations': the pack's maxItems, 0 for none, 1 or more for the host.",
            ["AgentMemory.Extensibility.Modules.PackTrialGate.CapProblem"], ["gate", "certify", "admit"],
            ["Review7GuardTests.M11_the_caps_every_bound_rests_on_are_the_declarations", "kit:certificate-none-baseline-offers", "kit:certificate-host-baseline-cap-zero"]),
        new("FLOOR-ON-EVIDENCE", "A host floor is judged on what a decision ran and checked, in the gate and the upgrade chain alike.",
            ["AgentMemory.Extensibility.Modules.PackTrialGate.Below", "AgentMemory.Extensibility.Modules.PackTrialPolicy.Of"], ["gate", "serve", "test"],
            ["Review7GuardTests.H6_a_host_floor_is_judged_on_the_tasks_a_decision_ran_not_on_what_its_pack_declares",
             "Review7GuardTests.M13_a_lift_floor_is_not_met_by_a_lift_against_a_baseline_that_offers_nothing",
             "TrialGateTests.Review6_a_host_floor_leaves_off_a_trial_that_claims_less"]),
        new("BOUNDS-COVER", "A bound passes a target at the truth by luck at most 1 - confidence, for every bound and the upgrade chain.",
            ["AgentMemory.Extensibility.Modules.PackTrialGate.Recheck"], ["gate", "certify", "admit"],
            ["TrialStatisticsTests.Review6_a_paired_hit_difference_at_its_target_passes_at_most_the_nominal_rate_exactly"],
            OpenFor: "R7-I6, R7-L15: exact coverage only for the paired hit difference; the other bounds are checked by Monte Carlo with loose ceilings"),
        new("PREVIOUS-CAP", "An upgrade's comparison judges the previous version's arm at its declared cap.",
            [], ["gate"], [],
            OpenFor: "found while fixing round 7's fixtures: the previous arm's cap is taken as recorded, and the host may not hold its declarations"),
        new("WALK-BOUNDED", "The walk's work is bounded inside a record, per record and per store read, and one record cannot fail the walk.",
            ["AgentMemory.Extensibility.Context.Traversal.TraversalWalk"], ["serve", "test"],
            ["WalkWorkBoundsTests.M3_the_in_memory_expansion_stops_with_the_budget"],
            OpenFor: "R7-H1, R7-H2, R7-H3, R7-M2, R7-L4"),
        new("STORES-AGREE", "The two stores offer the same records in the same order, at every cap.",
            ["AgentMemory.Extensibility.Context.Traversal.TraversalWalk"], ["serve", "test"], [],
            OpenFor: "R7-M1, R7-M3, R7-L1: the store differential is integration-only and never reaches a cap"),
        new("ONE-MOUNT-PLAN", "serve, test and inspect hold packs together by the host's rules: one version per id, a snapshot replacing its module's records.",
            [], ["serve", "test", "inspect"], [],
            OpenFor: "R7-H4, R7-M4, R7-M5"),
        new("SIGNED-BEFORE-ACT", "countersign, lock and set act only on a pack whose signatures hold.",
            [], ["countersign", "lock", "set"], [],
            OpenFor: "R7-L7"),
    ];

    [Fact]
    public void Every_rule_names_its_one_definition_and_guards_that_exist_and_every_door_is_bound_to_a_rule()
    {
        var root = Root();
        var problems = new List<string>();
        Assembly[] assemblies =
        [
            typeof(AgentMemory.Packs.Format.PackManifest).Assembly, typeof(AgentMemory.Extensibility.Modules.PackTrialGate).Assembly,
            typeof(AgentMemory.Extensibility.Ampack.PackLoader).Assembly,
        ];
        foreach (var rule in Rules)
        {
            foreach (var definition in rule.Definitions)
                if (!Resolves(definition, assemblies, root)) problems.Add($"{rule.Id}: its definition {definition} does not exist");
            foreach (var guard in rule.Guards)
                if (!GuardExists(guard, root)) problems.Add($"{rule.Id}: its guard {guard} does not exist");
            if (rule.Guards.Length == 0 && rule.OpenFor is null) problems.Add($"{rule.Id}: a rule with no guard is open, and says for what");
            if (rule.Definitions.Length == 0 && rule.OpenFor is null) problems.Add($"{rule.Id}: a rule with no one definition is open, and says for what");
            foreach (var door in rule.Doors)
                if (!PackDoors.Contains(door)) problems.Add($"{rule.Id}: {door} is not a door of the matrix");
        }
        foreach (var door in PackDoors.Where(d => !Rules.Any(r => r.Doors.Contains(d))))
            problems.Add($"{door}: a door bound to no rule");

        problems.Should().BeEmpty();
        Rules.Where(r => r.OpenFor is not null).Select(r => r.Id).Should().Equal(
            ["BOUNDS-COVER", "PREVIOUS-CAP", "WALK-BOUNDED", "STORES-AGREE", "ONE-MOUNT-PLAN", "SIGNED-BEFORE-ACT"],
            "the open rules are the ones the report says are open; closing one moves it here and there together");
    }

    private static bool Resolves(string definition, Assembly[] assemblies, string root)
    {
        if (definition.Contains('/')) return File.Exists(Path.Combine(root, definition));
        var dot = definition.LastIndexOf('.');
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        foreach (var assembly in assemblies)
        {
            if (assembly.GetType(definition) is not null) return true;
            if (assembly.GetType(definition[..dot]) is { } type && type.GetMember(definition[(dot + 1)..], All).Length > 0) return true;
        }
        return false;
    }

    private static bool GuardExists(string guard, string root)
    {
        if (guard.StartsWith("kit:", StringComparison.Ordinal))
            return File.ReadAllText(Path.Combine(root, "conformance", "pack-v1.1", "run_vectors.py")).Contains($"\"{guard[4..]}\"", StringComparison.Ordinal);
        if (guard.StartsWith("PackFormat:", StringComparison.Ordinal))
        {
            var (type, method) = Split(guard["PackFormat:".Length..]);
            var source = Path.Combine(root, "tests", "AgentMemory.Tests.Unit.PackFormat", type + ".cs");
            return File.Exists(source) && File.ReadAllText(source).Contains($"public void {method}(", StringComparison.Ordinal);
        }
        var (name, member) = Split(guard);
        var test = typeof(PackRuleRegistryTests).Assembly.GetType("AgentMemory.Tests.Unit.Extensibility." + name);
        return test?.GetMethod(member) is { } m && m.GetCustomAttributes().Any(a => a is FactAttribute);
    }

    private static (string Type, string Member) Split(string name) => (name[..name.IndexOf('.')], name[(name.IndexOf('.') + 1)..]);

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root holds AgentMemory.slnx.");
    }
}
