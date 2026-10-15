"""Inspect recorded native activities independently of producer assertion booleans. No services/models."""
import json
import re


def inspect(report, world, upgrade=None):
    checks = []
    def check(name, passed):
        checks.append({'name': name, 'passed': bool(passed)})
    trace = report.get('telemetry')
    check('trace/receipt-present', isinstance(trace, dict))
    if not isinstance(trace, dict):
        return checks
    spans = trace.get('spans', [])
    operations = trace.get('operations', [])
    check('trace/nonempty', bool(spans) and bool(operations))
    ids = [s.get('spanId') for s in spans + operations]
    check('trace/unique-ids', len(ids) == len(set(ids)) and all(re.fullmatch('[0-9a-f]{16}', i or '') for i in ids))
    parents = {s['spanId']: s.get('parentSpanId') for s in spans + operations}
    root = trace.get('rootSpanId')
    def reaches(child, ancestor):
        visited = set()
        while child in parents and child not in visited:
            visited.add(child)
            child = parents[child]
            if child == ancestor:
                return True
        return False
    check('trace/connected-acyclic', all(reaches(i, root) for i in ids))
    check('trace/one-shared-source', all(s.get('source') == 'AgentMemory' and s.get('traceId') == trace.get('traceId') for s in spans))
    modules = [s for s in spans if s.get('name', '').startswith('memory.module.')]
    families = {s['name'] for s in modules}
    for family in ('compile', 'contribute', 'ingest', 'migrate'):
        check('family/' + family, 'memory.module.' + family in families)
    by_id = {s['spanId']: s for s in spans}
    expected_module, expected_version = world['module'], report['packageVersion']
    fingerprint = report.get('moduleFingerprint')
    check('identity/resolution-fingerprint', bool(re.fullmatch('[0-9a-f]{64}', fingerprint or '')))
    for span in modules:
        tags = span.get('tags', {}); name = span['name']; sid = span['spanId']; prefix = 'span/' + sid + '/'
        owned = name in ('memory.module.ingest', 'memory.module.contribute') or tags.get('memory.module.operation') == 'step'
        check(prefix + 'identity', (tags.get('memory.module.id') == expected_module and tags.get('memory.module.version') == expected_version)
              if owned else 'memory.module.id' not in tags and 'memory.module.version' not in tags)
        if name != 'memory.module.contribute':
            check(prefix + 'fingerprint', tags.get('memory.module.fingerprint') == fingerprint and tags.get('memory.module.fingerprint.kind') == 'metadata')
        check(prefix + 'outcome', isinstance(tags.get('memory.module.outcome'), str) and bool(tags['memory.module.outcome']))
        if name == 'memory.module.contribute':
            parent = by_id.get(span.get('parentSpanId'), {})
            check(prefix + 'compile-parent', parent.get('name') == 'memory.module.compile')
            check(prefix + 'owned-contributor', tags.get('memory.contributor.id', '').startswith(expected_module + ':'))
        if name == 'memory.module.ingest':
            check(prefix + 'owned-provider', tags.get('memory.provider.id', '').startswith(expected_module + ':'))
        if tags.get('memory.module.operation') == 'step':
            check(prefix + 'one-owned-step-attempt', tags.get('memory.module.migration.steps') == 1)
            parent = by_id.get(span.get('parentSpanId'), {})
            check(prefix + 'apply-parent', parent.get('name') == 'memory.module.migrate' and parent.get('tags', {}).get('memory.module.operation') == 'apply')
    db = [s for s in spans if s['name'] == 'memory.db.tx']
    for family in ('contribute', 'ingest', 'migrate'):
        candidates = [s for s in modules if s['name'] == 'memory.module.' + family
                      and (family != 'migrate' or s.get('tags', {}).get('memory.module.operation') == 'step')]
        # These unchanged frozen forks skip primary queries and never select a view in
        # NegativeCases. Require NotApplicable AND no read, rather than inventing retrieval.
        no_retrieval = report['scenario'] in (
            'missing-source', 'foreign-source', 'wrong-owner-anchor', 'ambiguous-name',
            'unknown-command-field', 'assistant-command', 'ownerless-everyturn')
        if family == 'contribute' and no_retrieval:
            check('database/contribute-not-applicable', bool(candidates) and all(
                s.get('tags', {}).get('memory.module.outcome') == 'NotApplicable' for s in candidates))
            check('database/contribute-no-unrequested-read', not any(
                reaches(d['spanId'], s['spanId']) for d in db for s in candidates))
        else:
            check('database/' + family, any(reaches(d['spanId'], s['spanId']) for d in db for s in candidates))
    core = [o for o in operations if o['name'] == 'native.core.turn']
    if report['scenario'] != 'upgrade-v2':
        check('off/core-turns-observed', len(core) >= 2)
    check('off/no-module-descendants', all(not reaches(s['spanId'], o['spanId']) for s in modules for o in core))
    if report['scenario'] == 'primary':
        replay = [o for o in operations if o['name'] == 'native.replay']
        check('replay/observed', bool(replay) and any(s['name'] == 'memory.module.ingest'
              and s.get('tags', {}).get('memory.module.operation') == 'replay'
              and reaches(s['spanId'], o['spanId']) for s in modules for o in replay))
    if report['scenario'] == 'upgrade-v2':
        noops = [s for s in modules if s.get('tags', {}).get('memory.module.operation') == 'apply'
                 and s.get('tags', {}).get('memory.module.outcome') == 'NoOp']
        check('migration/repeat-noop-observed', bool(noops))
        check('migration/noop-read-transactions-observed', all(any(reaches(d['spanId'], s['spanId']) for d in db) for s in noops))
        check('migration/noop-read-only', all(d.get('tags', {}).get('db.mode') == 'read'
              for s in noops for d in db if reaches(d['spanId'], s['spanId'])))
        check('migration/noop-no-owned-step', all(not reaches(t['spanId'], s['spanId'])
              for s in noops for t in modules if t.get('tags', {}).get('memory.module.operation') == 'step'))
    # Inspect values rather than property names, using only actual sensitive inputs (not schema/operation words).
    private = set(world.get('owners', []))
    fields = {'owner', 'session', 'conversation', 'userMessageId', 'assistantMessageId', 'expectedRecordId',
              'aboutEntityId', 'title', 'description', 'detail', 'target', 'note', 'question', 'priority_reason'}
    def collect(value):
        if isinstance(value, dict):
            for key, child in value.items():
                if key in fields and isinstance(child, str) and child:
                    private.add(child)
                if key in ('expectedSourceIds', 'requiredText') and isinstance(child, list):
                    private.update(x for x in child if isinstance(x, str) and x)
                collect(child)
        elif isinstance(value, list):
            for child in value: collect(child)
    collect(world)
    if upgrade: collect(upgrade)
    values = [str(v) for s in modules for v in s.get('tags', {}).values()]
    values.extend(str(s.get('statusDescription', '')) for s in modules)
    values.extend(str(e.get('name', '')) for s in modules for e in s.get('events', []))
    values.extend(str(v) for s in modules for e in s.get('events', []) for v in e.get('tags', {}).values())
    check('privacy/no-input-values-in-module-tags-or-events', all(secret not in value for secret in private for value in values))
    return checks


def write_verdict(path, report, world, upgrade=None):
    checks = inspect(report, world, upgrade)
    result = {'protocol': 'module-native-telemetry/v1', 'scenario': report.get('scenario'),
              'passed': sum(c['passed'] for c in checks), 'failed': sum(not c['passed'] for c in checks), 'checks': checks}
    path.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    return result['failed'] == 0
