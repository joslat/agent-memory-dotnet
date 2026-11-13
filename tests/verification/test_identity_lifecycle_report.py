import importlib.util
from pathlib import Path
import tempfile
import copy
import unittest

root = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('identity_lifecycle_report', root / 'tools/AgentMemory.Scenarios/verification/identity_lifecycle_report.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class IdentityLifecycleReportTests(unittest.TestCase):
    corpus = {'name': 'test-corpus', 'cases': [{'id': 'DATE-01', 'family': 'date', 'requiredStages': ['identity', 'storage-bindings', 'retrieval']},
                                            {'id': 'CROSS-05', 'family': 'cross-cut', 'requiredStages': ['extraction']}]}
    def report(self, rows):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'results.trx'
            path.write_text('<TestRun><Results>' + rows + '</Results></TestRun>', encoding='utf-8')
            return module.produce_report(self.corpus, [path], 'baseline')
    def result(self, name, outcome='Passed', execution='one'):
        import html
        return '<UnitTestResult testName="' + html.escape(name, quote=True) + '" outcome="' + outcome + '" executionId="' + execution + '" />'
    def test_no_results_does_not_accept_any_stage(self):
        report = self.report('')
        self.assertFalse(report['accepted'])
        self.assertEqual(4, len(report['unverifiedRequiredStages']))
    def test_policy_pass_does_not_qualify_storage_or_retrieval(self):
        report = self.report(self.result('Example.IdentityLifecyclePolicyTests.Identity_typed(id: "DATE-01", json: "...")'))
        self.assertEqual('passed', report['cases'][0]['stages']['identity']['status'])
        self.assertFalse(report['accepted'])
        self.assertEqual(3, len(report['unverifiedRequiredStages']))
    def test_failed_quote_maps_to_extraction_without_claiming_proven_root_cause(self):
        report = self.report(self.result('Example.BarilocheWriterAuditReproductions.Invented_quote_needs_correction_without_accepting_unsupported_evidence','Failed'))
        self.assertEqual('extraction', report['failures'][0]['stage'])
        self.assertFalse(report['failures'][0]['rootCauseConfirmed'])
    def test_case_id_in_unrelated_test_text_does_not_create_coverage(self):
        report = self.report(self.result('Other.Test_DATE-01'))
        self.assertEqual(1, len(report['unmappedExecutedTests']))
        self.assertEqual(4, len(report['unverifiedRequiredStages']))
    def test_failure_cannot_be_hidden_by_a_passing_check_in_same_stage(self):
        name='Example.IdentityLifecyclePolicyTests.Identity_typed(id: "DATE-01", json: "...")'
        report = self.report(self.result(name,'Failed','a')+self.result(name,'Passed','b'))
        self.assertEqual('failed', report['cases'][0]['stages']['identity']['status'])
    def test_skipped_check_is_unverified(self):
        report=self.report(self.result('Example.IdentityLifecyclePolicyTests.Identity_typed(id: "DATE-01", json: "...")','NotExecuted'))
        self.assertEqual('unverified', report['cases'][0]['stages']['identity']['status'])
    def test_duplicate_execution_does_not_inflate_counts(self):
        row=self.result('Example.IdentityLifecyclePolicyTests.Identity_typed(id: "DATE-01", json: "...")')
        self.assertEqual(1,self.report(row+row)['counts']['passed'])


    @staticmethod
    def cell(variant, configuration="evidence-contextual", check="binding-roundtrip"):
        return {"stage":"identity", "configuration":configuration, "variant":variant, "check":check}
    def coverage_report(self, requirements, rows):
        original=self.corpus
        try:
            self.corpus={"name":"required-cell-corpus", "schemaVersion":2, "cases":[{
                "id":"DATE-01", "family":"date", "requiredStages":["identity"], "requiredCoverage":requirements}]}
            return self.report(rows)
        finally:
            self.corpus=original
    def named_cell(self, variant, configuration="evidence-contextual", check="binding-roundtrip", execution="one"):
        return self.result('Example.IdentityLifecycleIntegrationTests.Identity_check(id: "DATE-01", configuration: "'+configuration+'", variant: "'+variant+'", check: "'+check+'")',execution=execution)
    def test_one_passing_variant_cannot_qualify_an_absent_required_variant(self):
        report=self.coverage_report([self.cell("sequential"),self.cell("restart")],self.named_cell("sequential"))
        self.assertEqual("unverified",report["cases"][0]["stages"]["identity"]["status"])
    def test_another_configuration_cannot_fill_a_missing_required_configuration(self):
        report=self.coverage_report([self.cell("sequential")],self.named_cell("sequential",configuration="evidence-deterministic"))
        self.assertEqual("unverified",report["cases"][0]["stages"]["identity"]["status"])
    def test_a_missing_required_check_is_not_hidden_by_a_pass_in_the_same_variant(self):
        report=self.coverage_report([self.cell("sequential"),self.cell("sequential",check="source-roots")],self.named_cell("sequential"))
        self.assertEqual("unverified",report["cases"][0]["stages"]["identity"]["status"])
    def test_every_explicit_required_cell_can_qualify_the_case(self):
        report=self.coverage_report([self.cell("sequential"),self.cell("restart")],self.named_cell("sequential",execution="one")+self.named_cell("restart",execution="two"))
        self.assertTrue(report["accepted"])
    def test_duplicate_required_cells_are_invalid_instead_of_implicit_coverage(self):
        with self.assertRaises(ValueError):
            self.coverage_report([self.cell("sequential"),self.cell("sequential")],self.named_cell("sequential"))

    def test_legacy_case_without_explicit_required_cells_cannot_be_accepted(self):
        original=self.corpus
        try:
            self.corpus={"name":"legacy-corpus","cases":[{"id":"DATE-01","family":"date","requiredStages":["identity"]}]}
            report=self.report(self.result('Example.IdentityLifecyclePolicyTests.Identity_typed(id: "DATE-01", json: "...")'))
        finally:
            self.corpus=original
        self.assertEqual("passed",report["cases"][0]["stages"]["identity"]["status"])
        self.assertFalse(report["cases"][0]["accepted"])
        self.assertFalse(report["accepted"])
    def test_required_cell_for_a_stage_that_is_not_required_is_invalid(self):
        with self.assertRaises(ValueError):
            self.coverage_report([self.cell("sequential"),{**self.cell("sequential"),"stage":"retrieval"}],self.named_cell("sequential"))
    def test_required_stage_without_any_required_cell_is_invalid(self):
        original=self.corpus
        try:
            self.corpus={"name":"incomplete","schemaVersion":2,"cases":[{"id":"DATE-01","family":"date",
                "requiredStages":["identity","retrieval"],"requiredCoverage":[self.cell("sequential")]}]}
            with self.assertRaises(ValueError):
                self.report(self.named_cell("sequential"))
        finally:
            self.corpus=original
    def test_required_cell_with_an_undeclared_configuration_is_invalid(self):
        original=self.corpus
        try:
            self.corpus={"name":"declared","schemaVersion":2,"configurations":[{"id":"evidence-contextual"}],"cases":[{"id":"DATE-01",
                "family":"date","requiredStages":["identity"],"requiredCoverage":[self.cell("sequential",configuration="evidence-contextul")]}]}
            with self.assertRaises(ValueError):
                self.report(self.named_cell("sequential"))
        finally:
            self.corpus=original
    def test_a_failing_unrequired_cell_still_fails_the_stage(self):
        report=self.coverage_report([self.cell("sequential")],self.named_cell("sequential",execution="one")+
            self.result('Example.IdentityLifecycleIntegrationTests.Identity_check(id: "DATE-01", configuration: "evidence-contextual", variant: "restart", check: "binding-roundtrip")','Failed','two'))
        self.assertEqual("failed",report["cases"][0]["stages"]["identity"]["status"])
        self.assertFalse(report["accepted"])
    def test_an_unexecuted_repeat_of_a_required_cell_leaves_it_unverified(self):
        report=self.coverage_report([self.cell("sequential")],self.named_cell("sequential",execution="one")+
            self.result('Example.IdentityLifecycleIntegrationTests.Identity_check(id: "DATE-01", configuration: "evidence-contextual", variant: "sequential", check: "binding-roundtrip")','NotExecuted','two'))
        self.assertEqual("unverified",report["cases"][0]["stages"]["identity"]["status"])
        self.assertEqual(1,len(report["unverifiedRequiredCells"]))

    def bound_report(self, rows, tests=("Example.WriterContractTests.Unknown_action_is_corrected",)):
        original=self.corpus
        try:
            self.corpus={"name":"bound","schemaVersion":2,"cases":[{"id":"CROSS-05","family":"cross-cut","requiredStages":["extraction"],
                "requiredCoverage":[{"stage":"extraction","configuration":"evidence-contextual","variant":"unknown-action","check":"bounded-repair","tests":list(tests)}]}]}
            return self.report(rows)
        finally:
            self.corpus=original
    def test_an_explicitly_bound_test_qualifies_only_its_declared_cell(self):
        report=self.bound_report(self.result("Example.WriterContractTests.Unknown_action_is_corrected"))
        self.assertTrue(report["accepted"])
        self.assertEqual("passed",report["cases"][0]["requiredCells"][0]["status"])
    def test_a_similarly_named_unbound_test_does_not_qualify_a_bound_cell(self):
        report=self.bound_report(self.result("Example.WriterContractTests.Unknown_action_is_corrected_too"))
        self.assertFalse(report["accepted"])
        self.assertEqual(1,len(report["unmappedExecutedTests"]))
    def test_a_failing_bound_test_fails_its_cell_and_case(self):
        report=self.bound_report(self.result("Example.WriterContractTests.Unknown_action_is_corrected","Failed"))
        self.assertEqual("failed",report["cases"][0]["stages"]["extraction"]["status"])
        self.assertFalse(report["accepted"])
    def test_a_bound_cell_needs_every_bound_test_executed(self):
        report=self.bound_report(self.result("Example.A.One"),tests=("Example.A.One","Example.A.Two"))
        self.assertEqual("unverified",report["cases"][0]["requiredCells"][0]["status"])
    def test_a_method_bound_for_one_case_still_fills_the_cells_its_own_parameters_name(self):
        original=self.corpus
        method="Example.IdentityLifecycleNativeTests.Storage_check"
        try:
            self.corpus={"name":"both","schemaVersion":2,"cases":[
                {"id":"DATE-01","family":"date","requiredStages":["storage-bindings"],"requiredCoverage":[
                    {"stage":"storage-bindings","configuration":"evidence-deterministic","variant":"sequential","check":"binding-roundtrip"}]},
                {"id":"CROSS-01","family":"cross-cut","requiredStages":["storage-bindings"],"requiredCoverage":[
                    {"stage":"storage-bindings","configuration":"evidence-deterministic","variant":"sequential","check":"equivalent-final-identity","tests":[method]}]}]}
            report=self.report(self.result(method+'(id: "DATE-01", configuration: "evidence-deterministic", variant: "sequential", check: "binding-roundtrip")'))
        finally:
            self.corpus=original
        self.assertTrue(report["accepted"])
        self.assertEqual(1,report["counts"]["passed"])
    def test_a_bound_test_name_must_be_a_non_empty_string(self):
        with self.assertRaises(ValueError):
            self.bound_report("",tests=("",))

if __name__ == '__main__': unittest.main()
