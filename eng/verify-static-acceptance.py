"""EXT-static-acceptance: pack the whole chain into a fresh mapped feed, build the StaticAcceptance consumer against NuGet
packages only, check every loaded AgentMemory DLL against its nupkg, and run it on an explicitly disposable Neo4j."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', required=True, help='New evidence directory under artifacts; existing directories are refused.')
parser.add_argument('--sdk-version', default='0.1.0', help='The preview line version to pack (PreviewVersion).')
parser.add_argument('--build-only', action='store_true', help='Compile the consumer without service access; not acceptance.')
parser.add_argument('--live', action='store_true', help='Also run part C with the real lessons writer and JEV (EXT-live-e2e; spends).')
args = parser.parse_args()
if not args.build_only:
    uri = urlparse(os.environ.get('NEO4J_URI', ''))
    if os.environ.get('AGENTMEMORY_DISPOSABLE_NEO4J') != '1' or uri.scheme != 'bolt' or uri.hostname not in ('localhost', '127.0.0.1', '::1'):
        raise SystemExit('Explicit disposable loopback Bolt service required.')
    if not os.environ.get('NEO4J_PASSWORD'):
        raise SystemExit('Missing disposable service password.')
out = (ROOT / args.output).resolve()
if not out.is_relative_to((ROOT / 'artifacts').resolve()):
    raise SystemExit('Output must remain under artifacts.')
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


def sha(data): return hashlib.sha256(data).hexdigest()


def run(name, command, allow_failure=False):
    if command[:2] in (['dotnet', 'build'], ['dotnet', 'pack']):
        command = [*command, '--disable-build-servers', '-m:1']
    result = subprocess.run(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, encoding='utf-8', errors='replace')
    (out / (name + '.log')).write_text(result.stdout, encoding='utf-8')
    commands.append({'name': name, 'command': command, 'exitCode': result.returncode})
    (out / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n', encoding='utf-8')
    if result.returncode and not allow_failure:
        raise SystemExit(name + ' failed; inspect ' + str(out / (name + '.log')))
    return result


def chain(projects):
    """Every src project the given ones reference, transitively, from their ProjectReference items."""
    seen, todo = [], list(projects)
    while todo:
        project = todo.pop()
        if project in seen: continue
        seen.append(project)
        text = (ROOT / 'src' / project / (project + '.csproj')).read_text(encoding='utf-8')
        todo += re.findall(r'ProjectReference Include="\.\.[\\/]([^\\/"]+)[\\/]', text)
    return sorted(seen)


core_version = re.search(r'<Version>([^<]+)</Version>', (ROOT / 'Directory.Build.props').read_text(encoding='utf-8')).group(1)
preview = ['AgentMemory.Extensibility', 'AgentMemory.Extensibility.AgentFramework', 'AgentMemory.Extensibility.Neo4j', 'AgentMemory.Validation']
projects = chain(preview + ['AgentMemory.Gate', 'AgentMemory.Inference'])
version_property = '-p:PreviewVersion=' + args.sdk_version
for project in projects:
    run('build-' + project, ['dotnet', 'build', f'src/{project}/{project}.csproj', '-c', 'Release', version_property])
for project in projects:
    run('pack-' + project, ['dotnet', 'pack', f'src/{project}/{project}.csproj', '-c', 'Release', '--no-build', '--no-restore', '-o', str(feed), version_property])
props = ['-p:ModuleSdkVersion=' + args.sdk_version, '-p:CoreVersion=' + core_version, '-p:CommitmentsPackageVersion=1.0.0',
         '-p:RestoreConfigFile=' + str(out / 'NuGet.Config')]
run('pack-commitments', ['dotnet', 'pack', 'eng/module-consumers/Commitments/Commitments.csproj', '-c', 'Release', '-o', str(feed), *props])
run('pack-lessons', ['dotnet', 'pack', 'eng/module-consumers/RepositoryLessons/RepositoryLessons.csproj', '-c', 'Release', '-o', str(feed), *props])
# The knowledge pack (design 0.13 §K6-K7), signed with a developer key made for this run only; the host trusts its public half.
keys = out / 'keys'; keys.mkdir()
run('build-cli', ['dotnet', 'build', 'tools/AgentMemory.Cli/AgentMemory.Cli.csproj', '-c', 'Release'])
cli = str(ROOT / 'tools/AgentMemory.Cli/bin/Release/net10.0/AgentMemory.Cli.dll')
pack_bundle = ROOT / 'packs/AgentMemory.Packs.MartialArts/agentmemory'
run('pack-key', ['dotnet', cli, 'packs', 'keygen', '--private', str(keys / 'pack.private.pem'), '--public', str(keys / 'pack.public.pem')])
try:
    run('pack-sign', ['dotnet', cli, 'packs', 'sign', str(pack_bundle), '--key', str(keys / 'pack.private.pem')])
finally:
    (keys / 'pack.private.pem').unlink(missing_ok=True)
run('pack-verify', ['dotnet', cli, 'packs', 'verify', str(pack_bundle), '--developer-key', str(keys / 'pack.public.pem')])
run('pack-repository', ['dotnet', 'pack', 'src/AgentMemory.Packs.Repository/AgentMemory.Packs.Repository.csproj', '-c', 'Release', '-o', str(feed), version_property])
run('pack-martial-arts', ['dotnet', 'pack', 'packs/AgentMemory.Packs.MartialArts/AgentMemory.Packs.MartialArts.csproj', '-c', 'Release', '-o', str(feed)])
consumer = ROOT / 'eng/module-consumers/StaticAcceptance/StaticAcceptance.csproj'
run('build-consumer', ['dotnet', 'build', str(consumer), '-c', 'Release', *props])
assets = json.loads((consumer.parent / 'obj/project.assets.json').read_bytes())
if any(v['type'] == 'project' for v in assets['libraries'].values()):
    raise SystemExit('ProjectReference consumer bypass detected.')
binaries = consumer.parent / 'bin/Release/net10.0'
loaded = {}
for identity in assets['libraries']:
    package_id, version = identity.split('/')
    if not package_id.startswith('AgentMemory.'): continue
    archive = feed / f'{package_id}.{version}.nupkg'
    with zipfile.ZipFile(archive) as nupkg:
        dlls = [n for n in nupkg.namelist() if n.startswith('lib/net10.0/') and n.endswith('.dll')]
        for name in dlls:
            data = nupkg.read(name)
            if (binaries / Path(name).name).read_bytes() != data:
                raise SystemExit('Consumer DLL differs from its fresh nupkg: ' + name)
            loaded[Path(name).name] = {'package': package_id, 'version': version, 'sha256': sha(data)}
# The pack's bundle reached the output byte for byte from its nupkg.
with zipfile.ZipFile(feed / 'AgentMemory.Packs.MartialArts.1.0.0.nupkg') as nupkg:
    bundle = {n[len('agentmemory/'):]: nupkg.read(n) for n in nupkg.namelist() if n.startswith('agentmemory/') and not n.endswith('/')}
copied = binaries / 'agentmemory-packs/io.agentmemory.packs.martialarts'
on_disk = {p.relative_to(copied).as_posix(): p.read_bytes() for p in copied.rglob('*') if p.is_file()} if copied.is_dir() else {}
if on_disk != bundle:
    raise SystemExit('The knowledge pack bundle in the output differs from its nupkg.')
loaded['agentmemory-packs/io.agentmemory.packs.martialarts'] = {'package': 'AgentMemory.Packs.MartialArts', 'version': '1.0.0',
                                                                 'files': {k: sha(v) for k, v in sorted(bundle.items())}}
source = {p.relative_to(ROOT).as_posix(): sha(p.read_bytes()) for p in [*(ROOT / 'eng/module-consumers').rglob('*'), *(ROOT / 'packs').rglob('*')]
          if p.is_file() and 'bin' not in p.parts and 'obj' not in p.parts}
source['eng/verify-static-acceptance.py'] = sha(Path(__file__).read_bytes())
report = {'createdAtUtc': datetime.now(timezone.utc).isoformat(),
          'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
          'worktreeStatus': subprocess.check_output(['git', 'status', '--short'], cwd=ROOT, text=True),
          'coreVersion': core_version, 'sdkVersion': args.sdk_version, 'sourceHashes': source,
          'packages': {p.name: sha(p.read_bytes()) for p in sorted(feed.glob('*.nupkg'))}, 'loaded': loaded,
          'serviceExecuted': False, 'status': 'prepared'}
(out / 'package-proof.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
if args.build_only:
    print(json.dumps({'status': 'build_only', 'packages': len(report['packages']), 'loadedDlls': len(loaded)}))
    sys.exit(0)
os.environ['AGENTMEMORY_PACK_DEVELOPER_KEY'] = str(keys / 'pack.public.pem')
result = run('acceptance', ['dotnet', str(binaries / 'StaticAcceptance.dll'), '--output', str(out / 'primary'), *(['--live'] if args.live else [])],
             allow_failure=True)
receipt = out / 'primary/report.json'
report['serviceExecuted'] = True
if receipt.exists():
    acceptance = json.loads(receipt.read_bytes())
    report['passed'], report['failed'] = acceptance['passed'], acceptance['failed']
    report['status'] = 'passed' if result.returncode == 0 and acceptance['failed'] == 0 and acceptance['passed'] > 0 else 'failed'
else:
    report['status'] = 'failed'
(out / 'package-proof.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'status': report['status'], 'passed': report.get('passed'), 'failed': report.get('failed'), 'report': str(receipt)}))
sys.exit(0 if report['status'] == 'passed' else 1)
