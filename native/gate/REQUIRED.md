# The gate's tests that are not data

A binding is done when it passes every case of `native/gate/cases` (tiers A, B and C) on every RID it ships, and has
its own test for each item of the first table. The cases are language-neutral; the tests below are not.
`check_mapping.py` proves that each of the gate's original Python tests is a case's source or listed here.

## 1. Each binding tests these in its own language

They are about the binding, not the library: its loader, its handles, its async and threading, its packaging. The
Python test is the reference for what to check.

| Id | What the binding's own test shows | Python's |
|---|---|---|
| R-HANDLES | Context and job handles survive a round trip as unsigned 64-bit values (a JavaScript Number keeps only 53 bits: use BigInt) | `native/gate/tests/test_gate.py::Test1Library::test_handles_round_trip_as_64_bit_integers` |
| R-LOAD-PATH | The library is loaded from an explicit path when one is given, else from the package that carries it, and from nowhere else | `native/gate/tests/test_gate.py::Test1Library::test_the_library_comes_from_a_known_place` |
| R-LOAD-UNLOADABLE | A file that cannot be loaded is refused with an error naming the file, the platform and "built for this platform" | `native/gate/tests/test_gate.py::Test1Library::test_an_unloadable_file_is_refused_with_an_actionable_error` |
| R-LOAD-FOREIGN | A loadable library without the `am_v1_*` exports is refused as "not an AgentMemory native library" | `native/gate/tests/test_gate.py::Test1Library::test_a_library_without_the_exports_is_refused` |
| R-PACKAGE | The installed package carries `MANIFEST.json` matching its library (ABI, platform tag, SHA-256, licences, commit), and a damaged library is refused before it is loaded | `native/gate/tests/test_gate.py::Test1Library::test_an_installed_wheel_carries_a_manifest_that_matches_its_library` |
| R-NO-DOTNET | The target image has no .NET: the binding needs nothing but the library | `native/gate/tests/test_gate.py::Test1Library::test_the_target_image_has_no_dotnet` |
| R-FREE-NULL | Freeing a null buffer is a no-op through the binding's buffer handling | `native/gate/tests/test_gate.py::Test1Library::test_freeing_null_is_a_no_op` |
| R-ASYNC | The async client recalls, runs 16 recalls at once, and a cancelled call raises the language's cancellation | `native/gate/tests/test_gate.py::Test5Lifecycle::test_async_client_and_task_cancellation` |
| R-MEMORY | 500 recalls do not grow the process by 64 MiB (and report p50, p95) | `native/gate/tests/test_gate.py::Test5Lifecycle::test_many_recalls_keep_memory_bounded` |
| R-WAITER-CLOSE | A thread or task blocked in `am_v1_wait` is woken by closing the context, within seconds, with no job | `native/gate/tests/test_gate.py::Test5Waiter::test_closing_wakes_a_waiting_waiter` |
| B7 | Cancelling async calls at any point leaks no job: 300 cancellations leave the context usable | `native/gate/tests/test_gate.py::Test8ReviewFindings::test_b7_cancelling_async_calls_at_any_point_leaks_no_job` |
| B8 | A cancelled open does not keep the store: it is closed, and the next open succeeds | `native/gate/tests/test_gate.py::Test8ReviewFindings::test_b8_a_cancelled_open_does_not_keep_the_store` |
| B15 | `am_v1_state` with a null output returns INVALID_ARGUMENT and still zeroes the other outputs | `native/gate/tests/test_gate.py::Test8ReviewFindings::test_b15_state_with_a_null_output_still_clears_the_others` |

## 2. The Python harness tests these once per RID

They are the library's behaviour, and they need what a case cannot describe: a journal file written or damaged by
hand, a child process killed in the middle of a write, the schema tier's empty store, or the Python-only LangGraph
adapter. A binding does not repeat them; the Python harness runs them on every RID.

| Area | Test |
|---|---|
| Journal | `native/gate/writetests/test_writes.py::Test3Journal::test_accepted_but_never_started_runs_after_the_next_open` |
| Journal | `native/gate/writetests/test_writes.py::Test3Journal::test_a_torn_last_line_is_dropped_and_a_damaged_middle_line_refuses_the_open` |
| Journal | `native/gate/writetests/test_writes.py::Test3Journal::test_a_second_open_of_the_state_directory_is_busy` |
| Journal | `native/gate/writetests/test_writes.py::Test3Journal::test_a_write_running_when_the_process_dies_is_unknown_and_never_rerun` |
| Journal | `native/gate/writetests/test_writes.py::Test3Journal::test_the_journal_is_private_and_logs_hold_no_memory_text` |
| Privacy | `native/gate/writetests/test_writes.py::Test4Privacy::test_an_unknown_remember_can_be_forgotten_and_recall_resumes` |
| Privacy | `native/gate/writetests/test_writes.py::Test4Privacy::test_erase_deletes_everything_of_the_owner_purges_the_journal_and_nothing_comes_back` |
| Learning | `native/gate/writetests/test_writes.py::Test6Learning::test_a_write_killed_while_running_is_applied_once_after_reopening` |
| Learning | `native/gate/writetests/test_writes.py::Test6Learning::test_a_write_whose_journal_end_was_lost_is_found_by_its_receipt_and_never_runs_again` |
| LangGraph | `native/gate/writetests/test_writes.py::Test5LangGraph::test_put_remembers_get_returns_the_receipt_and_search_finds_the_memory` |
| LangGraph | `native/gate/writetests/test_writes.py::Test5LangGraph::test_the_same_words_again_run_nothing_and_other_words_replace_the_earlier_ones` |
| LangGraph | `native/gate/writetests/test_writes.py::Test5LangGraph::test_delete_forgets_and_the_key_reads_as_absent` |
| LangGraph | `native/gate/writetests/test_writes.py::Test5LangGraph::test_words_are_required_and_the_async_api_works` |
| Schema (tier S) | `native/gate/schematests/test_schema.py::Test1Profile::test_admin_offers_the_schema_operations_only` |
| Schema (tier S) | `native/gate/schematests/test_schema.py::Test1Profile::test_read_only_does_not_offer_the_schema` |
| Schema (tier S) | `native/gate/schematests/test_schema.py::Test2Bootstrap::test_1_the_bootstrap_makes_the_store_complete_at_the_configured_dimensions` |
| Schema (tier S) | `native/gate/schematests/test_schema.py::Test2Bootstrap::test_2_a_status_read_reports_other_dimensions_as_a_mismatch` |
