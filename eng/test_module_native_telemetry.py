"""Mutation tests for the independent native trace inspector. No database or model calls."""
import unittest
from module_native_telemetry import inspect


def fixture():
    root, trace_id = '0' * 16, 'a' * 32
    sid = lambda n: f'{n:016x}'
    metadata = {'memory.module.fingerprint': 'a' * 64, 'memory.module.fingerprint.kind': 'metadata', 'memory.module.outcome': 'Completed'}
    owned = {'memory.module.id': 'com.example.notes', 'memory.module.version': '1.0.0'}
    spans = []
    def span(n, parent, name, tags):
        spans.append({'spanId': sid(n), 'parentSpanId': parent, 'name': name, 'source': 'AgentMemory',
                      'traceId': trace_id, 'tags': tags, 'events': []})
    span(10, sid(1), 'memory.module.compile', metadata.copy())
    span(11, sid(10), 'memory.module.contribute', {**owned, 'memory.contributor.id': 'com.example.notes:items', 'memory.module.outcome': 'Section'})
    span(12, sid(11), 'memory.db.tx', {'db.mode': 'read'})
    span(13, sid(1), 'memory.module.ingest', {**metadata, **owned, 'memory.provider.id': 'com.example.notes:teach', 'memory.module.operation': 'ingest'})
    span(14, sid(13), 'memory.db.tx', {'db.mode': 'write'})
    span(15, root, 'memory.module.migrate', {**metadata, 'memory.module.operation': 'apply'})
    span(16, sid(15), 'memory.module.migrate', {**metadata, **owned, 'memory.module.operation': 'step', 'memory.module.migration.steps': 1})
    span(17, sid(16), 'memory.db.tx', {'db.mode': 'write'})
    span(18, sid(4), 'memory.module.ingest', {**metadata, **owned, 'memory.provider.id': 'com.example.notes:teach', 'memory.module.operation': 'replay'})
    operations = [{'name': name, 'spanId': sid(n), 'parentSpanId': root} for n, name in
                  [(1, 'native.module.turn'), (2, 'native.core.turn'), (3, 'native.core.turn'), (4, 'native.replay')]]
    return {'scenario': 'primary', 'packageVersion': '1.0.0', 'moduleFingerprint': 'a' * 64,
            'telemetry': {'rootSpanId': root, 'traceId': trace_id, 'spans': spans, 'operations': operations}}, {
                'module': 'com.example.notes', 'owners': ['private-owner'], 'teachSteps': [{'title': 'Private memory title'}]}


class TraceInspectorTests(unittest.TestCase):
    def test_valid_receipt(self):
        report, world = fixture()
        self.assertTrue(all(c['passed'] for c in inspect(report, world)))

    def test_nonretrieval_negative_forks_require_inapplicability_and_no_read(self):
        # These frozen forks teach and exercise write/source refusals; Program skips primary
        # queries and NegativeCases never selects a retrieval view for these scenarios.
        scenarios = ('missing-source', 'foreign-source', 'wrong-owner-anchor', 'ambiguous-name',
                     'unknown-command-field', 'assistant-command', 'ownerless-everyturn')
        for scenario in scenarios:
            with self.subTest(scenario=scenario):
                report, world = fixture(); report['scenario'] = scenario
                contribution = report['telemetry']['spans'][1]
                contribution['tags']['memory.module.outcome'] = 'NotApplicable'
                report['telemetry']['spans'].pop(2)
                self.assertTrue(all(c['passed'] for c in inspect(report, world)))
                # Do not excuse a searched view or silently allow an unexpected database read.
                contribution['tags']['memory.module.outcome'] = 'Section'
                self.assertFalse(all(c['passed'] for c in inspect(report, world)))
                report, world = fixture(); report['scenario'] = scenario
                report['telemetry']['spans'][1]['tags']['memory.module.outcome'] = 'NotApplicable'
                self.assertFalse(all(c['passed'] for c in inspect(report, world)))

    def test_retrieval_negative_forks_still_require_database_traces(self):
        for scenario in ('required-anchor-loss', 'session-clear', 'missing-anchor-before-limit',
                         'off-neutrality', 'runner-mutation-control'):
            with self.subTest(scenario=scenario):
                report, world = fixture(); report['scenario'] = scenario
                self.assertTrue(all(c['passed'] for c in inspect(report, world)))
                report['telemetry']['spans'].pop(2)
                self.assertFalse(all(c['passed'] for c in inspect(report, world)))

    def test_rejects_missing_receipt(self):
        report, world = fixture(); del report['telemetry']
        self.assertFalse(all(c['passed'] for c in inspect(report, world)))

    def test_rejects_mutations(self):
        changes = {
            'missing-family': lambda r: r['telemetry']['spans'].__setitem__(0, {**r['telemetry']['spans'][0], 'name': 'wrong'}),
            'version': lambda r: r['telemetry']['spans'][1]['tags'].__setitem__('memory.module.version', '9.0.0'),
            'fingerprint': lambda r: r['telemetry']['spans'][0]['tags'].__setitem__('memory.module.fingerprint', 'b' * 64),
            'private-owner': lambda r: r['telemetry']['spans'][0]['tags'].__setitem__('bad', 'private-owner'),
            'private-memory-event': lambda r: r['telemetry']['spans'][0]['events'].append({'name': 'bad', 'tags': {'detail': 'Private memory title'}}),
            'orphan': lambda r: r['telemetry']['spans'][0].__setitem__('parentSpanId', 'f' * 16),
            'cycle': lambda r: r['telemetry']['spans'][0].__setitem__('parentSpanId', r['telemetry']['spans'][1]['spanId']),
            'contribution-parent': lambda r: r['telemetry']['spans'][1].__setitem__('parentSpanId', '0' * 16),
            'off-module-work': lambda r: r['telemetry']['spans'][0].__setitem__('parentSpanId', r['telemetry']['operations'][1]['spanId']),
            'no-contribution-db': lambda r: r['telemetry']['spans'][2].__setitem__('parentSpanId', '0' * 16),
            'foreign-provider': lambda r: r['telemetry']['spans'][3]['tags'].__setitem__('memory.provider.id', 'com.foreign:teach'),
            'foreign-trace': lambda r: r['telemetry']['spans'][0].__setitem__('traceId', 'b' * 32),
            'missing-step-count': lambda r: r['telemetry']['spans'][6]['tags'].pop('memory.module.migration.steps'),
            'owned-set-work': lambda r: r['telemetry']['spans'][5]['tags'].update({'memory.module.id': 'com.example.notes', 'memory.module.version': '1.0.0'}),
        }
        for name, mutate in changes.items():
            with self.subTest(name=name):
                report, world = fixture(); mutate(report)
                self.assertFalse(all(c['passed'] for c in inspect(report, world)))

    def test_rejects_writes_or_steps_below_upgrade_noop(self):
        report, world = fixture(); report['scenario'] = 'upgrade-v2'
        report['telemetry']['spans'][5]['tags']['memory.module.outcome'] = 'NoOp'
        checks = inspect(report, world)
        self.assertIn('migration/noop-read-only', [c['name'] for c in checks if not c['passed']])
        self.assertIn('migration/noop-no-owned-step', [c['name'] for c in checks if not c['passed']])

    def test_noop_without_recorded_database_reads_is_not_proof(self):
        report, world = fixture(); report['scenario'] = 'upgrade-v2'
        report['telemetry']['spans'][5]['tags']['memory.module.outcome'] = 'NoOp'
        report['telemetry']['spans'][7]['parentSpanId'] = '0' * 16
        failed = [c['name'] for c in inspect(report, world) if not c['passed']]
        self.assertIn('migration/noop-read-transactions-observed', failed)



if __name__ == '__main__': unittest.main(verbosity=2)
