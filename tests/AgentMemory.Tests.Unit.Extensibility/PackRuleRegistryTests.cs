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
            ["PackFormat:StrictZipTests.A_bomb_is_refused_from_its_declared_ratio_before_inflating", "PackDoorMatrixTests.Every_command_meets_every_hostile_pack_with_a_refusal_or_an_answer_that_is_bounded_and_escaped", "PackFormat:StrictZipTests.Review9_D1_every_size_limit_refuses_the_archive_that_passes_it"]),
        new("FILE-BOUNDED", "Pack code reads a file only through the one bounded reader, or at a listed site with its reason.",
            ["AgentMemory.Packs.Format.BoundedFile.Read"], ["lint", "build", "sign", "migrate", "lock"],
            ["ChokePointRatchetTests.Pack_code_reads_parses_and_prints_only_through_its_choke_points_or_a_listed_site", "Review8GuardTests.A_file_one_byte_over_its_bound_is_refused_and_one_at_it_is_read"]),
        new("AUTHENTICATE-FIRST", "Nothing in a pack is parsed beyond its list and envelopes before its signatures hold, at every load path.",
            ["AgentMemory.Packs.Format.PackVerifier.VerifyOne", "AgentMemory.Extensibility.Ampack.PackLoader.Load"],
            ["verify", "test", "serve", "install", "receipt", "certify", "certificate", "admit", "gate"],
            ["AuthenticateFirstTests.H1_every_load_path_refuses_an_unsigned_pack_before_it_reads_pack_json", "ConformanceKitTests.Both_verifiers_reach_every_vector_s_expected_outcome_and_agree_on_seeded_mutants", "AuthenticateFirstTests.Review8_A12_a_pack_whose_signature_does_not_hold_is_refused_for_that_alone_at_every_load_path"]),
        new("UNVERIFIED-BOUNDED", "Content read without verifying it is bounded before it is parsed: 1 MiB a file or line, 64 MiB in all.",
            ["AgentMemory.Packs.Format.PackContentBounds.Problems"], ["diff", "scan", "lint"],
            ["Review7GuardTests.H7_content_read_without_verifying_it_is_bounded_before_it_is_parsed", "PackDoorMatrixTests.Every_command_meets_every_hostile_pack_with_a_refusal_or_an_answer_that_is_bounded_and_escaped", "PackDoorMatrixTests.Review8_lint_bounds_what_nobody_verified_before_it_parses_any_of_it", "ChokePointRatchetTests.Pack_code_reads_parses_and_prints_only_through_its_choke_points_or_a_listed_site", "PackFormat:StrictZipTests.Review9_D2_a_json_file_over_one_mebibyte_is_refused_before_it_is_parsed_verified_or_not"]),
        new("ONE-WRITER-SIGN", "Every signature passes the writer's rules: a writer signs only the minor it writes.",
            ["AgentMemory.Packs.Format.PackBuilder.Sign"], ["build", "sign", "countersign", "set", "certify"],
            ["Review7GuardTests.M6_every_signature_holds_the_writer_s_rules_not_build_alone", "PackFormat:ReadableMinorTests.A_writer_signs_only_the_minor_it_writes", "Review10GuardTests.A_M2_the_writer_never_signs_at_a_time_every_reader_refuses"]),
        new("ONE-WRITER-ARCHIVE", "Every archive a writer returns is read back under the reader's own limits and checks first.",
            ["AgentMemory.Packs.Format.PackBuilder.Pack"], ["build", "countersign", "set", "certify"],
            ["Review7GuardTests.M7_the_one_writer_never_writes_an_archive_a_reader_refuses", "Review8WriterGuardTests.F1_an_envelope_every_reader_refuses_is_refused_by_the_writer_and_by_lint", "Review8WriterGuardTests.F2_a_set_every_reader_refuses_is_refused_by_the_writer_and_by_lint", "PackFormat:FederationTests.Review9_A1_a_federated_sets_member_rules_need_no_key_so_the_writer_and_lint_hold_them_too", "ChokePointRatchetTests.Pack_code_reads_parses_and_prints_only_through_its_choke_points_or_a_listed_site"]),
        new("OUTPUT-ESCAPED", "Every line the tool prints leaves control and bidi characters as escapes.",
            ["tools/Ampack/ShownWriter.cs"], PackDoors,
            ["PackDoorMatrixTests.Every_command_meets_every_hostile_pack_with_a_refusal_or_an_answer_that_is_bounded_and_escaped",
             "ShownWriterTests.Controls_bidi_and_a_bare_carriage_return_leave_as_escapes_and_line_ends_pass", "ShownWriterTests.The_tool_installs_it_on_both_streams"]),
        new("VERIFIERS-AGREE", "The two verifiers reach one verdict on every input either reads.",
            ["AgentMemory.Packs.Format.PackVerifier.VerifyOne", "conformance/pack-v1.1/python/ampack_verify.py"], ["verify"],
            ["ConformanceKitTests.Both_verifiers_reach_every_vector_s_expected_outcome_and_agree_on_seeded_mutants", "kit:deflate-damaged"]),
        new("CERTIFICATE-COPIES", "A statement carried in several envelopes is trusted when any copy verifies; additions never take trust away.",
            ["AgentMemory.Extensibility.Ampack.PackCertificates.Read", "AgentMemory.Extensibility.Ampack.PackCertificates.Pooled"], ["inspect", "certificate", "admit", "test", "serve"],
            ["PackCertificateTests.Review7_H5_a_broken_copy_of_a_trusted_certificate_never_hides_it_whatever_its_name", "AmpackCommandLineTests.A_card_travels_in_the_pack_and_an_evaluator_s_certificate_beside_it_until_a_host_that_trusts_it_admits_the_pack"]),
        new("CERTIFICATE-RUN-BOUND", "An AEF run travels beside a certificate only as the evidence of its own decision: its subject names this pack at this digest and version.",
            ["AgentMemory.Packs.Format.PackAttestations.RunIsOf"], ["certify", "certificate", "inspect", "admit"],
            ["PackCertificateTests.Review10_B10_M1_a_certificate_carries_only_the_run_of_its_own_decision", "kit:certificate-aef-run-of-another-pack",
             "kit:certificate-aef-run-json-outside-profile"]),
        new("PROFILES-DERIVED", "The profiles a pack uses are derived from what it holds by the registry's one profile map, the same in both verifiers; a host refuses exactly the used profiles the map marks not implemented.",
            ["AgentMemory.Packs.Format.PackProfiles.Used"], ["verify", "serve", "test", "install"],
            ["PackFormat:PackProfilesTests.A_pack_uses_the_profiles_whose_files_fields_or_values_it_holds_in_the_registry_s_order",
             "PackFormat:PackProfilesTests.A_host_refuses_exactly_the_profiles_the_registry_marks_not_implemented_in_their_own_words",
             "PackReviewFindingsTests.A_pack_that_declares_protection_the_host_cannot_open_is_refused", "PackReviewFindingsTests.A_pack_that_carries_code_is_refused_until_the_host_runs_it_and_a_malformed_usage_policy_by_every_reader", "kit:profile-walk", "kit:profile-code-tier"]),
        new("INTEGER-UNITS", "Every unit-bearing number of a pack document is an integer in its registry unit and range; a decimal is refused at reading, never rounded.",
            ["AgentMemory.Packs.Format.PackNumbers.Integer", "AgentMemory.Packs.Format.PackNumbers.MulFloor"], ["lint", "verify", "serve"],
            ["ModuleEditorSchemaTests.TraversalAndTrialEdgesHaveTheSameEditorAndRuntimeContract",
             "PackUpgradeTests.A_trial_declares_its_upgrade_rule_and_a_tolerance_out_of_range_is_refused",
             "PackFormat:PackNumbersTests.A_product_of_millionths_is_rounded_down", "TraversalTests.A_folder_s_quality_is_whole_millionths_rounded_down"]),
        new("TEXT-BY-CHARACTER", "Every text bound counts characters (code points) and every order is code-point order, in both verifiers and the walk.",
            ["AgentMemory.Packs.Format.PackText.Length", "AgentMemory.Packs.Format.PackText.Compare"], ["verify", "serve", "test"],
            ["PackFormat:PackTextTests.A_length_counts_characters_not_UTF16_units", "PackFormat:PackTextTests.The_order_is_UTF8_byte_order_on_every_generated_pair",
             "WalkCharactersTests.Tied_records_are_in_the_format_s_order_by_code_point", "kit:card-astral-at-limit", "kit:federated-purpose-300-astral",
             "AnchorMatcherWorkTests.An_equality_is_charged_by_characters_so_a_late_anchor_still_seeds_after_emoji"]),
        new("EXACT-VERDICT", "Whether a trial's target is met is decided exactly, in rational arithmetic, the same in every language; floating point only shows a bound.",
            ["AgentMemory.Extensibility.Modules.PackTrialGate.MeetsRate", "AgentMemory.Extensibility.Modules.PackTrialGate.MeetsPrecision",
             "AgentMemory.Extensibility.Modules.PackTrialGate.MeetsHitDifference", "AgentMemory.Extensibility.Modules.PackTrialGate.MeetsPrecisionDifference"],
            ["gate", "admit", "certify"],
            ["TrialStatisticsTests.Every_exact_verdict_is_the_reference_s", "TrialStatisticsTests.A_bound_exactly_at_its_target_is_decided_the_same_everywhere",
             "TrialGateTests.A_lift_exactly_at_its_target_is_met_exactly", "TrialGateTests.A_bound_exactly_at_its_target_is_met_through_the_decision",
             "TrialGateTests.A_safety_target_is_met_only_when_the_model_followed_at_most_its_share_of_the_probes",
             "TrialGateTests.A_cost_target_is_met_only_when_the_pack_saves_its_target_on_enough_tasks", "kit:certificate-cost-met-below-its-target",
             "TrialGateTests.The_cost_check_reaches_the_reference_s_verdicts", "StatisticsCoverageTests.Golden_cost_json_is_the_reference_s_own_output",
             "kit:certificate-cost-unmet-at-its-target",
             "StatisticsCoverageTests.Golden_json_is_the_reference_s_own_output"]),
        new("EMBEDDINGS-STRICT", "A pack's vectors are one tensor per set in the safetensors layout, exactly as their signed index states, naming the pack's records; a host reuses them only on an exact model match.",
            ["AgentMemory.Packs.Format.PackEmbeddings.Read", "AgentMemory.Packs.Format.PackEmbeddings.Reusable"], ["verify", "serve"],
            ["PackEmbeddingsTests.Vectors_are_reused_only_on_an_exact_match_of_the_model_that_made_them",
             "PackEmbeddingsTests.A_pack_with_vectors_composes_the_same_module_and_records_as_without_so_the_walk_cannot_see_them",
             "kit:embeddings-second-tensor", "kit:embeddings-metadata", "kit:embeddings-valid",
             "kit:embeddings-row-of-a-line-without-a-key", "kit:embeddings-beside-a-line-nested-too-deep"]),
        new("DISPLAY-SAFE", "Every text a pack shows a person is display text: no code point the registry refuses (controls but line feed and tab, format characters but ZWNJ and ZWJ, private use, surrogates), not blank, and one line holds no line or paragraph separator, in both verifiers and the host.",
            ["AgentMemory.Packs.Format.PackText.IsDisplaySafe", "AgentMemory.Packs.Format.PackText.IsDisplayText"], ["verify", "lint", "inspect"],
            ["PackFormat:PackTextTests.Display_safe_text_refuses_what_hides_or_reorders_what_it_shows", "kit:card-bidi-override", "kit:display-title-bidi-override",
             "kit:card-emoji-joined-by-zwj", "kit:display-title-blank", "kit:display-title-line-separator", "kit:card-owner-blank",
             "kit:federated-purpose-bidi-override", "kit:display-description-paragraph-separator"]),
        new("FRAMED-RECORDS", "Every module item reaches a model inside a frame, on every path that renders it (a pack's record naming its pack, version, type and key, any other its module), escaped, after the admission of untrusted content, whatever its trust.",
            ["src/AgentMemory.Core/Security/RecalledMemoryDelimiter.cs", "AgentMemory.Extensibility.Context.ContextFrames.Of"], ["serve"],
            ["ModuleDelimitingTests.A_pack_record_is_framed_with_its_source_and_cannot_forge_another", "ModuleDelimitingTests.ModuleTextCannotForgeTheMemoryBoundary",
             "NativeModuleSelectionTests.Selection_frames_a_pack_record_as_the_sections_path_does"]),
        new("COUNTED-IS-SHOWN", "The fixed cost a trial counts for a pack is the framed bytes of what it shows the model on every turn (its card's whenToUse, at most 400 characters), defined once.",
            ["AgentMemory.Extensibility.Modules.PackTrialRender.AlwaysShown"], ["gate", "verify"],
            ["WhenToUseTests.A_card_s_when_to_use_reaches_the_module_and_its_digest", "WhenToUseTests.A_trial_counts_the_text_against_every_task_s_saving",
             "kit:card-when-to-use-401", "kit:card-when-to-use-400-emoji"]),
        new("LIVE-IN-WORLD-TIME", "A record takes part in a view or a walk only while it is true in the world at the request's valid time, or now: one definition, applied where the walk seeds, steers and offers and where a view's records leave the store.",
            ["AgentMemory.Extensibility.Reading.ModuleRecordLife.IsLive"], ["serve"],
            ["RecordLifeTests.A_walk_offers_a_record_only_while_it_is_true_in_the_world",
             "RecordLifeTests.The_module_set_s_reader_keeps_the_records_true_at_its_pin_and_asks_the_store_at_it",
             "RecordLifeTests.A_superseded_record_ends_where_its_successor_begins", "RecordLifeTests.A_refreshing_record_ends_its_type_s_days_after_it_begins",
             "RecordLifeTests.A_record_past_its_life_never_takes_a_live_neighbour_s_place_in_the_fan_out", "RecordLifeTests.A_snapshot_resolves_its_records_lives_and_its_digest_names_them"]),
        new("ERASED-STAYS-GONE", "A record an erasure names never reaches a store or a reader again: erasures are applied before an import writes, a tombstone of the salted key hash blocks every later copy, and memory never holds it.",
            ["AgentMemory.Extensibility.Packs.ModuleErasure.Erases", "AgentMemory.Packs.Format.PackErasure.Verify"], ["install", "serve", "verify"],
            ["ErasureTests.A_key_hash_is_the_salted_sha256_both_verifiers_and_the_stores_compute", "ErasureTests.Memory_never_holds_an_erased_record_and_links_into_it_are_left_out",
             "ErasureTests.An_import_applies_the_erasures_that_name_its_pack_before_it_writes", "kit:erasure-by-an-evaluator", "kit:erasure-by-salted-hash",
             "ErasureTests.A_module_set_never_offers_a_record_an_erasure_it_holds_names_whatever_the_store_holds",
             "ErasureTests.An_authored_erasure_verifies_names_no_key_and_its_receipt_names_nothing_else",
             "ErasureTests.A_loaded_pack_keeps_the_erasures_it_carries_that_verify_and_no_other",
             "ErasureTests.A_loaded_packs_import_applies_the_erasures_it_carries_first", "kit:erasure-issued-in-other-digits"]),
        new("TRIAL-OVER-TIME", "A trial decision counts only for the days the host's floor gives it: past them the pack is off until trialled again, and within them it goes silent when they end.",
            ["AgentMemory.Extensibility.Modules.PackTrialGate.Apply"], ["serve", "gate"],
            ["TrialGateTests.A_decision_counts_for_the_days_the_host_s_floor_gives_it_and_then_switches_the_pack_off"]),
        new("POLICY-AT-THE-DOOR", "A pack's usage policy is read strictly and alike by both verifiers, deny wins and an ended rule grants nothing, and each door asks it before it acts: read at verify, export at serve, rehydrate where a module set offers records to the host's model.",
            ["AgentMemory.Extensibility.Ampack.PolicyGate.Allows", "AgentMemory.Packs.Format.PackPolicy.Parse"], ["verify", "serve", "install"],
            ["PolicyTests.Deny_wins_and_an_ended_rule_stops_granting_but_never_stops_refusing",
             "PolicyTests.A_rule_for_named_WebIDs_speaks_only_to_a_host_acting_as_one_of_them",
             "PolicyTests.A_module_set_withholds_a_pack_s_records_from_a_model_its_policy_keeps_them_from",
             "AmpackCommandLineTests.Serve_refuses_a_pack_whose_usage_policy_does_not_let_this_host_export_it",
             "PolicyTests.Read_is_a_condition_of_every_other_verb_and_the_loader_names_the_policy_on_every_record",
             "SnapshotPackTests.A_snapshot_s_records_name_the_usage_policy_of_the_pack_that_carried_them",
             "PackFamilyTests.A_family_member_s_usage_policy_is_held_as_an_added_pack_s_is",
             "kit:set-carries-a-policy", "kit:composite-carries-a-policy",
             "kit:policy-deny-beats-allow", "kit:policy-read-ended", "kit:policy-ended-denial-still-denies", "kit:policy-unknown-verb"]),
        new("TRUSTED-NO-FURTHER", "A pack's derivation is read strictly and alike by both verifiers, and a host offers a record at a confidence no higher than its own, its source input's or its signer's, and none below its floor.",
            ["AgentMemory.Extensibility.Packs.ModuleConfidence.Effective", "AgentMemory.Packs.Format.PackDerivation.Parse"], ["install", "serve", "verify"],
            ["ConfidenceTests.The_effective_confidence_never_exceeds_any_of_its_parts", "ConfidenceTests.A_host_trusts_a_record_no_further_than_its_source_and_its_signer",
             "ConfidenceTests.A_source_key_that_names_no_input_refuses_the_pack",
             "ConfidenceTests.The_floor_leaves_out_records_below_it_and_the_frame_says_how_far_a_record_is_trusted",
             "ConfidenceTests.Every_role_the_policy_requires_caps_a_record_through_its_most_trusted_signer",
             "ConfidenceTests.A_store_keeps_the_floor_before_its_limit_and_the_host_s_floor_reaches_it",
             "ConfidenceTests.A_walk_neither_seeds_from_nor_offers_a_record_below_the_floor",
             "ConfidenceTests.Only_a_signed_pack_s_records_carry_a_confidence_and_a_source_key",
             "ConfidenceTests.Nothing_but_the_importer_writes_a_confidence_and_a_module_cannot_declare_its_stored_name",
             "kit:derivation-not-canonical", "kit:derivation-confidence-above-a-million"]),
        new("CERTIFICATE-STATES-ALL", "A certificate is issued only for a trial whose every declared target it states, with the evidence to derive it again (each task's bytes for a cost check): a target only a host can decide (safety) refuses issuance, never drops from it.",
            ["AgentMemory.Extensibility.Ampack.PackCertificates.Issue"], ["certify"],
            ["PackCertificateTests.A_trial_with_a_safety_target_is_never_certified", "PackCertificateTests.A_cost_target_is_certified_and_admitted_with_each_task_s_bytes",
             "kit:certificate-bytes-from-an-arm-offering-nothing", "kit:certificate-bytes-past-a-billion", "kit:certificate-bytes-negative"]),
        new("EVIDENCE-INTAKE", "Every verdict, made or rechecked, passes one intake: same tasks, consistent arms, one spacing within the window.",
            ["AgentMemory.Extensibility.Modules.PackTrialGate.Intake", "AgentMemory.Extensibility.Modules.PackTrialGate.Recheck"], ["gate", "certify", "admit", "serve"],
            ["Review7GuardTests.L11_every_arm_of_a_trial_was_drawn_at_one_spacing_within_the_history_window", "Review7GuardTests.L16_a_recheck_runs_the_intake_the_decision_ran",
             "kit:certificate-arms-spaced-differently", "kit:certificate-spacing-past-window"]),
        new("DECISION-COUNTS", "The decision that counts for a content is the host's latest own one, else, only where it trusts certificates, for a pack added alone, while its certificate is the latest trusted; the upgrade chain counts own trials only.",
            ["AgentMemory.Extensibility.Modules.PackTrialGate.Counting", "AgentMemory.Extensibility.Modules.PackTrialGate.Apply"], ["gate", "serve", "admit"],
            ["PackCertificateTests.An_admission_counts_only_while_its_certificate_is_the_latest_trusted_one_the_pack_carries",
             "PackCertificateTests.Review2_the_serve_gate_counts_a_certificate_for_a_pack_alone_but_never_for_a_family_member",
             "Review8GuardTests.G2_only_this_host_s_own_trials_stand_as_the_version_an_upgrade_replaces"]),
        new("CAPS-DECLARED", "The caps every bound rests on are the declarations': the pack's maxItems, 0 for none, 1 or more for the host.",
            ["AgentMemory.Extensibility.Modules.PackTrialGate.CapProblem"], ["gate", "certify", "admit"],
            ["Review7GuardTests.M11_the_caps_every_bound_rests_on_are_the_declarations", "kit:certificate-none-baseline-offers", "kit:certificate-host-baseline-cap-zero", "PackCertificateTests.Review8_A2_an_admission_holds_a_certificate_s_arms_to_the_caps_the_pack_declares"]),
        new("FLOOR-ON-EVIDENCE", "A host floor is judged on what a decision ran and checked, in the gate and the upgrade chain alike.",
            ["AgentMemory.Extensibility.Modules.PackTrialGate.Below", "AgentMemory.Extensibility.Modules.PackTrialPolicy.Of"], ["gate", "serve"],
            ["TrialGateTests.A_safety_target_is_reachable_its_run_sound_and_a_host_floor_can_require_it",
             "Review7GuardTests.H6_a_host_floor_is_judged_on_the_tasks_a_decision_ran_not_on_what_its_pack_declares",
             "Review7GuardTests.M13_a_lift_floor_is_not_met_by_a_lift_against_a_baseline_that_offers_nothing",
             "TrialGateTests.Review6_a_host_floor_leaves_off_a_trial_that_claims_less", "Review8GuardTests.A7_the_upgrade_chain_passes_over_a_version_below_the_host_floor", "Review8GuardTests.A3_the_module_set_judges_a_host_floor_on_the_tasks_a_decision_ran", "AmpackCommandLineTests.A_card_travels_in_the_pack_and_an_evaluator_s_certificate_beside_it_until_a_host_that_trusts_it_admits_the_pack"]),
        new("BOUNDS-COVER", "A bound passes a target at the truth by luck at most 1 - confidence: exactly over every outcome for independent tasks, and for tasks in bursts of at most 8, at the regime's worst case per layout, within Monte Carlo error.",
            ["AgentMemory.Extensibility.Modules.PackTrialGate.Recheck", "eng/trial-statistics/reference.py"], ["gate", "certify", "admit"],
            ["StatisticsCoverageTests.Every_bound_passes_a_target_at_the_truth_at_most_its_nominal_rate_exactly_over_every_outcome",
             "TrialStatisticsTests.Review6_a_paired_hit_difference_at_its_target_passes_at_most_the_nominal_rate_exactly",
             "StatisticsCoverageTests.Every_clustered_bound_holds_its_nominal_rate_at_the_burst_regime_s_worst_case", "StatisticsCoverageTests.Golden_json_is_the_reference_s_own_output"]),
        new("PREVIOUS-CAP", "An upgrade's comparison holds the previous version's arm at the cap this host judged that version at.",
            ["AgentMemory.Extensibility.Modules.PackTrialGate.Against"], ["gate"],
            ["PackUpgradeTests.Review7_an_upgrade_holds_the_version_it_replaces_at_the_cap_this_host_judged_it_at"]),
        new("WALK-BOUNDED", "The walk's work is bounded inside a record, per record and per store read, and one record cannot fail the walk.",
            ["AgentMemory.Extensibility.Context.Traversal.AnchorMatcher.MaximumWork", "AgentMemory.Extensibility.Context.Traversal.TraversalWalk"], ["serve", "test"],
            ["AnchorMatcherWorkTests.W1_one_value_of_many_pieces_against_many_anchors_costs_at_most_the_work_bound",
             "AnchorMatcherWorkTests.W1_a_run_of_dot_slashes_and_white_space_normalizes_in_linear_time",
             "AnchorMatcherWorkTests.W2_a_glob_piece_longer_than_any_regex_engine_limit_is_matched_not_thrown",
             "TraversalBudgetTests.Review7_W2_one_module_s_failing_read_leaves_that_module_out_and_the_walk_goes_on",
             "TraversalDeadlineTests.A_read_with_a_budget_runs_under_that_server_deadline_and_one_without_runs_as_before",
             "TraversalDeadlineTests.The_server_stopping_a_statement_at_the_deadline_is_a_stop_by_time",
             "WalkWorkBoundsTests.M3_the_in_memory_expansion_stops_with_the_budget", "Review8GuardTests.W2_neo4j_expansion_returns_narrow_rows_and_reads_whole_only_the_records_it_keeps", "AnchorMatcherWorkTests.Review8_W1_a_value_at_the_format_s_longest_line_is_bounded_in_memory_and_time_whatever_it_holds", "AnchorMatcherWorkTests.Review8_W1_ordinary_names_are_charged_what_they_compare_so_a_late_anchor_still_seeds"],
            OpenFor: "R7-M2: built (candidates streamed into a bounded best-k), but no guard yet measures the Neo4j read's memory"),
        new("PATH-AND-GLOB", "A path normalizes, and a glob matches, exactly as the format defines them.",
            ["AgentMemory.Extensibility.Context.Traversal.AnchorMatcher.Normalized", "AgentMemory.Extensibility.Context.Traversal.AnchorMatcher.GlobPattern"], ["serve", "test"],
            ["AnchorMatcherWorkTests.The_path_in_one_pass_equals_the_loop_the_format_defines_on_every_generated_value",
             "AnchorMatcherWorkTests.The_glob_matcher_equals_the_regex_it_replaced_on_every_generated_pair", "AnchorMatcherWorkTests.Review8_W1_a_value_at_the_format_s_longest_line_is_bounded_in_memory_and_time_whatever_it_holds", "AnchorMatcherWorkTests.Review8_W1_ordinary_names_are_charged_what_they_compare_so_a_late_anchor_still_seeds", "Review8GuardTests.W2_neo4j_expansion_returns_narrow_rows_and_reads_whole_only_the_records_it_keeps"]),
        new("STORES-AGREE", "The two stores offer the same records in the same order, at every cap: every candidate scanned, the walk making each cut.",
            ["AgentMemory.Extensibility.Context.Traversal.TraversalWalk"], ["serve", "test"],
            ["WalkWorkBoundsTests.Review7_W7_the_walk_makes_the_fan_out_cut_whatever_a_store_returns_and_in_whatever_order",
             "WalkWorkBoundsTests.Review7_W10_what_the_walk_could_not_do_is_listed_in_one_order_whatever_the_store_s"],
            OpenFor: "the store differential runs on Neo4j only and never at a cap: no guard compares the stores where the caps bind"),
        new("ONE-MOUNT-PLAN", "serve, test and gate hold packs together by the host's rules: one version per id, one live snapshot per module, a snapshot refused before the dependency its links reach.",
            ["AgentMemory.Extensibility.Ampack.PackFamilies.HostComposes", "AgentMemory.Extensibility.Ampack.PackMountPlan.Of"], ["serve", "test", "gate", "install"],
            ["AmpackLoaderTests.Review7_W4_packs_are_held_together_as_a_host_holds_them_one_version_of_each",
             "PackConsentTests.Review7_W9_a_snapshot_whose_links_reach_a_dependency_that_holds_nothing_is_refused_as_the_store_refuses_it",
             "Integration:ModuleRecordWriteIntegrationTests.Review7_W9_AWorkSnapshotImportedBeforeTheCodeMapIsRefusedNotLeftWithoutItsLinks", "AmpackLoaderTests.Review8_W3_copies_of_one_pack_are_held_by_one_rule_whatever_their_order", "Review9GuardTests.C_H1_copies_of_one_pack_are_held_by_the_host_s_rule_whatever_their_order", "Review8GuardTests.A5_the_gate_holds_packs_together_by_the_mount_plan", "Review8GuardTests.A5_two_snapshots_of_one_module_are_refused_whoever_holds_them", "Review10GuardTests.C_M2_a_family_member_counts_through_the_route_a_host_composes_it_by", "Review10GuardTests.R11_every_door_holds_what_the_host_composes_and_refuses_what_it_refuses", "AmpackLoaderTests.Review11_serve_holds_what_the_host_composes_whatever_the_packaging", "PackFamilyTests.Review11_serve_refuses_a_family_the_host_refuses", "PackFamilyTests.Review12_a_pack_a_family_carries_switched_off_is_held_once_when_given_on_its_own", "PackConsentTests.Review12_serve_holds_one_live_snapshot_of_a_module", "PackConsentTests.Review13_the_store_installs_only_what_serve_then_holds", "PackConsentTests.Review14_install_runs_the_snapshot_imports_serve_runs", "Review10GuardTests.R12_4_each_pack_comes_from_the_route_that_registered_it"]),
        new("LIVE-PER-RECORD", "A record takes part in a walk only while its module and the pack that carried it do: it neither seeds, steers nor offers.",
            ["AgentMemory.Extensibility.Context.Traversal.TraversalWalk.RunAsync"], ["serve"],
            ["WalkWorkBoundsTests.Review7_W8_a_record_that_may_not_take_part_neither_seeds_nor_steers", "Review8GuardTests.A4_a_pack_memory_walk_leaves_out_the_records_an_expired_carrier_brought"]),
        new("SIGNED-BEFORE-ACT", "countersign, lock and set, and their library twins, act only on a pack verified whole under the host's policy (a set with its members).",
            ["AgentMemory.Packs.Format.PackVerifier.VerifyWhole", "AgentMemory.Packs.Format.PackVerifier.Before",
             "AgentMemory.Extensibility.Ampack.PackAuthoring.Countersign", "AgentMemory.Extensibility.Ampack.PackAuthoring.BuildComposite",
             "AgentMemory.Extensibility.Ampack.PackAuthoring.BuildSet"], ["countersign", "lock", "set"],
            ["PackDoorMatrixTests.Review7_L7_countersign_lock_and_set_act_only_on_a_pack_whose_signatures_hold",
             "Review8WriterGuardTests.F4_the_library_acts_only_on_packs_its_host_s_trust_verifies"]),
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

        string.Join(Environment.NewLine, problems).Should().BeEmpty();
        Rules.Where(r => r.OpenFor is not null).Select(r => r.Id).Should().Equal(
            ["WALK-BOUNDED", "STORES-AGREE"],
            "the open rules are the ones the report says are open; closing one moves it here and there together");
    }

    // Review 8's meta-root: a guard was checked to exist, never to fail with its rule broken, so guards passed with their rule
    // removed at a door. Every rule has recorded mutants (eng/guards/mutants.json), each breaking it at its definition or at a
    // door; each names a rule held here; and the last recorded run (mutants-results.json, written by eng/guards/mutants.py)
    // saw the rule's guards fail on every one. A new mutant fails this until a run kills it.
    [Fact]
    public void Every_rule_has_mutants_and_a_recorded_run_saw_its_guards_kill_each_one()
    {
        var root = Root();
        using var mutants = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "eng", "guards", "mutants.json")));
        using var results = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "eng", "guards", "mutants-results.json")));
        var recorded = mutants.RootElement.GetProperty("mutants").EnumerateArray()
            .Select(m => (Id: m.GetProperty("id").GetString()!, Rule: m.GetProperty("rule").GetString()!, File: m.GetProperty("file").GetString()!,
                Hash: m.GetProperty("sha256").GetString()!)).ToList();
        var byId = results.RootElement.GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!, r => r.Clone());
        var problems = new List<string>();
        foreach (var (id, rule, file, hash) in recorded)
        {
            var rerun = $"(python eng/guards/mutants.py {id})";
            if (Rules.FirstOrDefault(r => r.Id == rule) is not { } held) { problems.Add($"{id}: breaks {rule}, which the registry does not hold"); continue; }
            if (!byId.TryGetValue(id, out var result) || result.GetProperty("killed").ValueKind != System.Text.Json.JsonValueKind.True)
            {
                problems.Add($"{id}: no recorded run saw its guards kill it {rerun}");
                continue;
            }
            // Review 9, D5: a catch is evidence for the file and the breakage it was run on, not for whatever is there now.
            if (!result.TryGetProperty("mutant_sha256", out var ran) || ran.GetString() != hash)
                problems.Add($"{id}: the breakage changed since its catch was recorded {rerun}");
            if (!result.TryGetProperty("file_sha256", out var was) || was.GetString() != Normalized(Path.Combine(root, file)))
                problems.Add($"{id}: {file} changed since its catch was recorded {rerun}");
            // Review 9, D6: caught by one of the rule's own guards, not by an unrelated test in the same filter.
            var failed = result.TryGetProperty("failed", out var f) ? f.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
            var guards = held.Guards.Select(g => g.StartsWith("kit:", StringComparison.Ordinal) ? "kit" : g[(g.IndexOf(':') + 1)..])
                .Select(g => g.Contains('.') ? string.Join('.', g.Split('.')[^2..]) : g).ToHashSet(StringComparer.Ordinal);
            if (!failed.Any(guards.Contains))
                problems.Add($"{id}: caught by {(failed.Count == 0 ? "no named test" : string.Join(", ", failed))}, none of them a guard of {rule}");
        }
        foreach (var rule in Rules.Where(r => !recorded.Any(m => m.Rule == r.Id)))
            problems.Add($"{rule.Id}: no mutant, so nothing shows its guards fail when it breaks");

        string.Join(Environment.NewLine, problems).Should().BeEmpty();
    }

    // A file's SHA-256 as the runner records it: without a BOM, with LF line ends, so a checkout's line ends change nothing.
    private static string Normalized(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal))));

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
        // An integration guard runs on Neo4j, in the integration suite: named by its class and method, found in its source.
        if (guard.StartsWith("Integration:", StringComparison.Ordinal))
        {
            var (type, method) = Split(guard["Integration:".Length..]);
            return Directory.EnumerateFiles(Path.Combine(root, "tests", "AgentMemory.Tests.Integration"), "*.cs", SearchOption.AllDirectories)
                .Any(f => File.ReadAllText(f).Contains($"class {type}", StringComparison.Ordinal) && File.ReadAllText(f).Contains($"public async Task {method}(", StringComparison.Ordinal));
        }
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
