"""Classify actual identity lifecycle test outcomes without treating missing stages as passes."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

AUDIT = {
    'Candidate_source_roots_need_hydration_before_contextual_decision': ('EVENT-01', 'candidate-context'),
    'Explicit_prior_context_control_reaches_the_resolver_without_changing_original_roots': ('EVENT-01', 'identity'),
    'Missing_context_control_keeps_uncertainty_instead_of_merging_by_name': ('EVENT-01', 'identity'),
    'Event_writer_needs_a_constrained_operation_response_format': ('CROSS-05', 'extraction'),
    'Invalid_entity_action_needs_bounded_correction_instead_of_a_silent_missing_event': ('CROSS-05', 'extraction'),
    'Invented_quote_needs_correction_without_accepting_unsupported_evidence': ('CROSS-05', 'extraction'),
}
STAGES = {
    'Identity_': 'identity', 'Context_': 'candidate-context', 'Storage_': 'storage-bindings',
    'Relationships_': 'relationships', 'Retrieval_': 'retrieval', 'TemporalRetrieval_': 'temporal-retrieval',
    'Writer_': 'extraction', 'Prompt_': 'prompt', 'Answer_': 'answer',
}
CELL_FIELDS = ('stage', 'configuration', 'variant', 'check')
DEFAULT_REQUIRED_STAGES = ['identity', 'storage-bindings', 'retrieval']
PARAMETER = re.compile(r'(\w+):\s*"((?:[^"\\]|\\.)*)"')
INVESTIGATE = {
    'candidate-context': 'Inspect candidate membership, offered source IDs/text, permission, chronology, completeness and byte/call budgets.',
    'identity': 'Inspect typed values, host-key authority, hard contradictions, ambiguity, policy and exact adjudicator input/output.',
    'storage-bindings': 'Inspect committed facts, decisions, original observations, role bindings, source roots and atomic acknowledgement.',
    'relationships': 'Inspect original relationship claims, endpoint decisions, review versions and ownership of each support.',
    'retrieval': 'Compare stored fact IDs with actual returned IDs; inspect time window, confirmed identity path, scope and limits.',
    'temporal-retrieval': 'Compare stored fact IDs with actual returned IDs; inspect time window, confirmed identity path, scope and limits.',
    'extraction': 'Inspect actual operation JSON, exact source quotes, dropped operations, event references and bounded correction outcome.',
    'prompt': 'Compare actual recalled IDs/evidence with the serialized prompt and shared context budget.',
    'answer': 'Review factual answer claims against the actual prompt; distinguish missing evidence from unsupported model conclusions.',
}

def classify(test_name: str, case_ids: set[str]):
    for method, classification in AUDIT.items():
        if ('BarilocheContextAuditReproductions.' + method in test_name or
                'BarilocheWriterAuditReproductions.' + method in test_name):
            return classification
    if 'IdentityLifecycle' not in test_name:
        return None
    match = re.search(r'\(id:\s*"([A-Z]+-\d+)"', test_name)
    if not match or match.group(1) not in case_ids:
        return None
    method = test_name.split('(')[0].rsplit('.', 1)[-1]
    stage = next((stage for prefix, stage in STAGES.items() if method.startswith(prefix)), None)
    return (match.group(1), stage) if stage else None

def cell_of(test_name: str, stage: str):
    """Explicit (stage, configuration, variant, check) cell named by the test's own parameters, else None."""
    if '(' not in test_name:
        return None
    values = {}
    for key, value in PARAMETER.findall(test_name.split('(', 1)[1]):
        if key in values:
            return None
        values[key] = value
    if not all(values.get(field) for field in CELL_FIELDS[1:]):
        return None
    return (stage, values['configuration'], values['variant'], values['check'])

def required_cells(corpus: dict, case: dict):
    """Validated mandatory cells for one case, or None when the case declares no explicit coverage."""
    explicit = corpus.get('schemaVersion', 1) >= 2 or 'requiredCoverage' in case
    if not explicit:
        return None
    case_id = case['id']
    stages = case.get('requiredStages', DEFAULT_REQUIRED_STAGES)
    producible = set(STAGES.values()) | {stage for _, stage in AUDIT.values()}
    declared_stages = set(corpus['stages']) if 'stages' in corpus else None
    declared_configurations = {c['id'] for c in corpus['configurations']} if 'configurations' in corpus else None
    for stage in stages:
        if stage not in producible:
            raise ValueError(f'{case_id}: required stage {stage!r} cannot be produced by any classified test')
        if declared_stages is not None and stage not in declared_stages:
            raise ValueError(f'{case_id}: required stage {stage!r} is not declared by the corpus')
    cells = case.get('requiredCoverage')
    if not isinstance(cells, list) or not cells:
        raise ValueError(f'{case_id}: explicit coverage requires a non-empty requiredCoverage list')
    keys = []
    for cell in cells:
        if not isinstance(cell, dict) or any(not isinstance(cell.get(f), str) or not cell[f].strip() for f in CELL_FIELDS):
            raise ValueError(f'{case_id}: every required cell needs non-empty {", ".join(CELL_FIELDS)}')
        if cell['stage'] not in stages:
            raise ValueError(f'{case_id}: required cell names stage {cell["stage"]!r}, which is not a required stage')
        if declared_configurations is not None and cell['configuration'] not in declared_configurations:
            raise ValueError(f'{case_id}: required cell names undeclared configuration {cell["configuration"]!r}')
        if 'tests' in cell and (not isinstance(cell['tests'], list) or not cell['tests'] or
                                any(not isinstance(t, str) or not t.strip() or '(' in t for t in cell['tests'])):
            raise ValueError(f'{case_id}: bound tests must be a non-empty list of exact method names without arguments')
        key = tuple(cell[f] for f in CELL_FIELDS)
        if key in keys:
            raise ValueError(f'{case_id}: duplicate required cell {key}')
        keys.append(key)
    uncovered = [stage for stage in stages if not any(key[0] == stage for key in keys)]
    if uncovered:
        raise ValueError(f'{case_id}: required stages without any required cell: {uncovered}')
    return keys

def status_of(checks: list[dict]) -> str:
    return ('failed' if any(c['status'] == 'failed' for c in checks) else
            'passed' if checks and all(c['status'] == 'passed' for c in checks) else 'unverified')

def produce_report(corpus: dict, paths: list[Path], role: str) -> dict:
    registered = {case['id']: case for case in corpus['cases']}
    if len(registered) != len(corpus['cases']):
        raise ValueError('Duplicate corpus case IDs')
    requirements = {case_id: required_cells(corpus, case) for case_id, case in registered.items()}
    # Exact test-method bindings declared by the corpus: an executed method counts only for the cells that name it.
    bound = {}
    bound_tests = {}
    for case_id, case in registered.items():
        for cell in case.get('requiredCoverage', []) or []:
            key = tuple(cell[f] for f in CELL_FIELDS)
            for test in cell.get('tests', []):
                bound.setdefault(test, []).append((case_id, key))
                bound_tests.setdefault((case_id, key), set()).add(test)
    outcomes = {case_id: {} for case_id in registered}
    unmapped, inputs, failures = [], [], []
    seen = set()
    counts = {'passed': 0, 'failed': 0, 'unexecuted': 0}
    for path in paths:
        raw = path.read_bytes()
        tree = ET.fromstring(raw)
        inputs.append({'path': str(path.resolve()), 'sha256': hashlib.sha256(raw).hexdigest()})
        for result in tree.iter():
            if result.tag.rsplit('}', 1)[-1] != 'UnitTestResult':
                continue
            unique = (str(path.resolve()), result.get('executionId') or result.get('testId') or result.get('testName'))
            if unique in seen:
                continue
            seen.add(unique)
            name = result.get('testName', '')
            status = {'Passed': 'passed', 'Failed': 'failed'}.get(result.get('outcome'), 'unexecuted')
            method = name.split('(', 1)[0]
            if method in bound:
                counts[status] += 1
                messages = [e.text or '' for e in result.iter() if e.tag.rsplit('}', 1)[-1] == 'Message']
                for case_id, key in bound[method]:
                    observation = {'testName': name, 'status': status, 'trx': str(path.resolve()),
                                   'executionId': result.get('executionId'), 'boundTest': method,
                                   'cell': dict(zip(CELL_FIELDS, key)), 'message': '\n'.join(messages)[:4096]}
                    outcomes[case_id].setdefault(key[0], []).append(observation)
                    if status == 'failed':
                        failures.append({'caseId': case_id, 'stage': key[0], 'testName': name, 'evidence': observation,
                                         'rootCauseConfirmed': False, 'nextInvestigation': INVESTIGATE.get(key[0], 'Inspect the bound test.')})
            # A bound method still fills the cells its own parameters name (a typed pair's native run is also CROSS-01's).
            classification = classify(name, set(registered))
            if classification is None:
                if method not in bound:
                    unmapped.append({'testName': name, 'status': status})
                continue
            case_id, stage = classification
            if case_id not in registered:
                raise ValueError('Test maps to a case absent from this corpus: ' + case_id)
            if method not in bound:
                counts[status] += 1
            messages = [e.text or '' for e in result.iter() if e.tag.rsplit('}', 1)[-1] == 'Message']
            cell = cell_of(name, stage)
            observation = {'testName': name, 'status': status, 'trx': str(path.resolve()),
                           'executionId': result.get('executionId'),
                           'cell': dict(zip(CELL_FIELDS, cell)) if cell else None,
                           'message': '\n'.join(messages)[:4096]}
            outcomes[case_id].setdefault(stage, []).append(observation)
            if status == 'failed':
                failures.append({'caseId': case_id, 'stage': stage, 'testName': name,
                                 'evidence': observation, 'rootCauseConfirmed': False,
                                 'nextInvestigation': INVESTIGATE[stage]})
    cases, missing_cells = [], []
    for case_id, case in registered.items():
        required = case.get('requiredStages', DEFAULT_REQUIRED_STAGES)
        keys = requirements[case_id]
        cells = []
        for key in keys or []:
            checks = [c for c in outcomes[case_id].get(key[0], []) if c['cell'] and tuple(c['cell'][f] for f in CELL_FIELDS) == key]
            status = status_of(checks)
            # A bound cell passes only when every test it names was executed and passed.
            required_tests = bound_tests.get((case_id, key), set())
            if status == 'passed' and not required_tests <= {c.get('boundTest') for c in checks if c['status'] == 'passed'}:
                status = 'unverified'
            cells.append({**dict(zip(CELL_FIELDS, key)), 'status': status, 'checks': checks,
                          'boundTests': sorted(required_tests)})
        stages = {}
        for stage in dict.fromkeys([*required, *outcomes[case_id]]):
            checks = outcomes[case_id].get(stage, [])
            status = status_of(checks)
            if keys is not None and status != 'failed' and stage in required:
                stage_cells = [c for c in cells if c['stage'] == stage]
                status = 'passed' if all(c['status'] == 'passed' for c in stage_cells) else 'unverified'
            stages[stage] = {'status': status, 'required': stage in required, 'checks': checks}
        blockers = []
        if keys is None:
            blockers.append('no-explicit-required-coverage')
        blockers += [f'stage-{data["status"]}:{stage}' for stage, data in stages.items()
                     if data['status'] == 'failed' or (data['required'] and data['status'] != 'passed')]
        missing_cells += [{'caseId': case_id, **{f: c[f] for f in CELL_FIELDS}, 'status': c['status']}
                          for c in cells if c['status'] != 'passed']
        cases.append({'caseId': case_id, 'family': case['family'], 'explicitCoverage': keys is not None,
                      'requiredCells': cells, 'stages': stages, 'accepted': not blockers, 'acceptanceBlockers': blockers})
    missing = [{'caseId': case['caseId'], 'stage': stage} for case in cases for stage, data in case['stages'].items()
               if data['required'] and data['status'] == 'unverified']
    return {'format': 'identity-lifecycle-report-v2', 'role': role, 'corpus': corpus['name'],
            'accepted': bool(cases) and all(c['accepted'] for c in cases) and not failures,
            'counts': counts, 'registeredCases': len(cases), 'cases': cases, 'failures': failures,
            'unverifiedRequiredStages': missing, 'unverifiedRequiredCells': missing_cells,
            'casesWithoutExplicitCoverage': [c['caseId'] for c in cases if not c['explicitCoverage']],
            'unmappedExecutedTests': unmapped, 'inputs': inputs,
            'semanticPrecision': None, 'falseMergeRate': None,
            'limitations': ['Classifies executed objective assertions; a failing stage is not an automatically proven root cause.',
                            'Missing, skipped and unmapped checks do not qualify a required stage.',
                            'A case is accepted only when it declares explicit stage x configuration x variant x check cells and every '
                            'cell has executed checks that all passed; a check naming no cell, or another cell, never fills a required one.',
                            'Model precision and false-merge rates require labeled identity outcomes; test pass counts are not those metrics.',
                            'Only explicitly selected TRX inputs participate; retain historical baseline reports separately.']}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--corpus', type=Path, required=True)
    parser.add_argument('--trx', type=Path, action='append', default=[])
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--role', choices=['baseline', 'validation'], default='baseline')
    args = parser.parse_args()
    if args.output.exists():
        parser.error('Refusing to overwrite an existing result; use a new output path.')
    corpus_raw = args.corpus.read_bytes()
    report = produce_report(json.loads(corpus_raw), args.trx, args.role)
    report['corpusSha256'] = hashlib.sha256(corpus_raw).hexdigest()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'accepted': report['accepted'], 'counts': report['counts'],
                      'unverifiedRequiredStages': len(report['unverifiedRequiredStages']),
                      'unverifiedRequiredCells': len(report['unverifiedRequiredCells']),
                      'casesWithoutExplicitCoverage': len(report['casesWithoutExplicitCoverage']), 'output': str(args.output)}))
    return 0 if report['accepted'] else 1

if __name__ == '__main__':
    raise SystemExit(main())
