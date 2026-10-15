"""Build and execute independent packaged Commitments consumers. No database/model calls or publishing."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', required=True, help='New evidence directory under artifacts; existing directories are refused.')
args = parser.parse_args()
out = (ROOT / args.output).resolve()
if not out.is_relative_to((ROOT / 'artifacts').resolve()):
    raise SystemExit('Output must remain under repository artifacts.')
out.mkdir(parents=True, exist_ok=False)
feed = out / 'feed'; feed.mkdir()
config = ET.Element('configuration'); sources = ET.SubElement(config, 'packageSources'); ET.SubElement(sources, 'clear')
ET.SubElement(sources, 'add', {'key': 'local', 'value': str(feed)})
ET.SubElement(sources, 'add', {'key': 'nuget.org', 'value': 'https://api.nuget.org/v3/index.json'})
mapping = ET.SubElement(config, 'packageSourceMapping')
local = ET.SubElement(mapping, 'packageSource', {'key': 'local'}); ET.SubElement(local, 'package', {'pattern': 'AgentMemory.*'})
external = ET.SubElement(mapping, 'packageSource', {'key': 'nuget.org'}); ET.SubElement(external, 'package', {'pattern': '*'})
settings = ET.SubElement(config, 'config'); ET.SubElement(settings, 'add', {'key': 'globalPackagesFolder', 'value': str(out / 'packages')})
ET.indent(config); ET.ElementTree(config).write(out / 'NuGet.Config', encoding='utf-8', xml_declaration=True)
commands = []
def run(name, command):
    process = subprocess.run(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, encoding='utf-8', errors='replace')
    (out / (name + '.log')).write_text(process.stdout, encoding='utf-8')
    commands.append({'name': name, 'command': command, 'exitCode': process.returncode})
    (out / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n', encoding='utf-8')
    if process.returncode:
        raise SystemExit(f'{name} failed; inspect {out / (name + ".log")}')
    return process.stdout

def sha(data): return hashlib.sha256(data).hexdigest()
run('build-sdk', ['dotnet', 'build', 'src/AgentMemory.Extensibility/AgentMemory.Extensibility.csproj', '-c', 'Release'])
for project in ('AgentMemory.Abstractions', 'AgentMemory.Extensibility'):
    run('pack-' + project, ['dotnet', 'pack', f'src/{project}/{project}.csproj', '-c', 'Release', '--no-build', '--no-restore', '-o', str(feed)])
checks = ROOT / 'eng/module-consumers/Commitments.Checks/Commitments.Checks.csproj'
package = ROOT / 'eng/module-consumers/Commitments/Commitments.csproj'
results = []
for version in ('1.0.0', '2.0.0'):
    props = ['-p:ModuleSdkVersion=0.1.0', '-p:CommitmentsPackageVersion=' + version, '-p:RestoreConfigFile=' + str(out / 'NuGet.Config')]
    run('pack-' + version, ['dotnet', 'pack', str(package), '-c', 'Release', '-o', str(feed), *props])
    run('build-checks-' + version, ['dotnet', 'build', str(checks), '-c', 'Release', *props])
    assets_path = checks.parent / 'obj/project.assets.json'
    assets = json.loads(assets_path.read_text(encoding='utf-8'))
    if any(v['type'] == 'project' for v in assets['libraries'].values()): raise SystemExit('ProjectReference consumer bypass detected.')
    (out / ('assets-' + version + '.json')).write_bytes(assets_path.read_bytes())
    for tfm in ('net8.0', 'net9.0', 'net10.0'):
        stdout = run('checks-' + version + '-' + tfm, ['dotnet', 'run', '--project', str(checks), '-c', 'Release', '-f', tfm, '--no-build', '--no-restore', *props])
        result = json.loads(stdout.strip().splitlines()[-1])
        if result['packageVersion'] != version or result['failed'] or not result['checks']: raise SystemExit('Incomplete or wrong-version consumer result.')
        result['tfm'] = tfm
        result['loadedHashes'] = {}
        for package_id, package_version, output_key in [('AgentMemory.Examples.Commitments', version, 'assembly'), ('AgentMemory.Extensibility', '0.1.0', 'sdk')]:
            with zipfile.ZipFile(feed / f'{package_id}.{package_version}.nupkg') as archive:
                expected = archive.read(f'lib/{tfm}/{package_id}.dll')
            actual = Path(result[output_key]).read_bytes()
            if actual != expected: raise SystemExit('Loaded DLL does not match freshly packed package: ' + package_id)
            result['loadedHashes'][package_id] = sha(actual)
            metadata = json.loads((out / 'packages' / package_id.lower() / package_version / '.nupkg.metadata').read_text(encoding='utf-8'))
            if Path(metadata['source']).resolve() != feed: raise SystemExit('Package came from an unexpected feed.')
        results.append(result)
packages = {p.name: {'sha256': sha(p.read_bytes()), 'bytes': p.stat().st_size} for p in sorted(feed.glob('*.nupkg'))}
source = {p.relative_to(ROOT).as_posix(): sha(p.read_bytes()) for p in (ROOT / 'eng/module-consumers').rglob('*')
          if p.is_file() and 'bin' not in p.parts and 'obj' not in p.parts}
source['eng/verify-commitments-package.py'] = sha(Path(__file__).read_bytes())
report = {'createdAtUtc': datetime.now(timezone.utc).isoformat(), 'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
          'worktreeStatus': subprocess.check_output(['git', 'status', '--short'], cwd=ROOT, text=True), 'sourceHashes': source,
          'packages': packages, 'runs': results, 'passed': sum(r['passed'] for r in results), 'failed': 0,
          'limits': ['Deterministic package contract checks; ProbeReader is a test double.', 'No database, native host, migration, signing or dynamic lifecycle acceptance.', 'No model or coding benchmark runs.']}
(out / 'report.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'versions': 2, 'frameworks': 3, 'runs': len(results), 'passed': report['passed'], 'failed': 0, 'report': str(out / 'report.json')}))
