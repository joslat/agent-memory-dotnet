"""Run a packaged v2 consumer on the retained successful v1 primary store; hosted runners only."""
import argparse
from module_native_telemetry import write_verdict
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from urllib.parse import urlparse
import xml.etree.ElementTree as ET
import zipfile

ROOT=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser(description=__doc__)
for name in ('prepared','world','world-sha256','spec','spec-sha256'): p.add_argument('--'+name,required=True)
a=p.parse_args()
if os.environ.get('GITHUB_ACTIONS')!='true':
    p.error('Retained-data upgrade execution is restricted to the isolated hosted acceptance job.')
uri=urlparse(os.environ.get('NEO4J_URI',''))
if os.environ.get('AGENTMEMORY_DISPOSABLE_NEO4J')!='1' or uri.scheme!='bolt' or uri.hostname not in ('localhost','127.0.0.1','::1') or not os.environ.get('NEO4J_PASSWORD'):
    p.error('Explicit disposable loopback Bolt service and test password required.')
def sha(path): return hashlib.sha256(Path(path).read_bytes()).hexdigest()
world=Path(a.world).resolve(); spec=Path(a.spec).resolve()
if sha(world)!=a.world_sha256 or sha(spec)!=a.spec_sha256: p.error('Frozen input hash mismatch.')
proof_path=Path(a.prepared).resolve(); prepared=json.loads(proof_path.read_bytes()); base=proof_path.parent
if not base.is_relative_to((ROOT/'artifacts').resolve()): p.error('Prepared evidence must be under artifacts.')
if prepared['status']!='primary_and_negatives_passed_acceptance_incomplete' or not prepared['negativeCasesExecuted']:
    p.error('Primary and every negative case must pass before upgrading the retained store.')
for name,digest in prepared['sourceHashes'].items():
    if sha(ROOT/name)!=digest: p.error('Prepared source changed before upgrade: '+name)
primary_path=base/'primary/report.json'; primary=json.loads(primary_path.read_bytes())
if primary['status']!='primary_passed_acceptance_incomplete' or primary['failed'] or primary['worldSha256']!=a.world_sha256:
    p.error('Successful exact-world primary receipt required.')
if not any(e['kind']=='retained-upgrade-snapshot' for e in primary['evidence']): p.error('Primary has no retained graph snapshot.')
old_runtime=Path(prepared['consumerAssembly']['path']).parent
if sha(old_runtime/'Commitments.NativeChecks.dll')!=prepared['consumerAssembly']['sha256']: p.error('V1 consumer identity changed.')
for package,identity in prepared['loadedPackages'].items():
    if sha(old_runtime/(package+'.dll'))!=identity['sha256']: p.error('V1 dependency identity changed: '+package)
out=base/'upgrade-build'; out.mkdir(exist_ok=False)
feed=out/'feed'; feed.mkdir()
for package,identity in prepared['loadedPackages'].items():
    if package=='AgentMemory.Examples.Commitments': continue
    name=package+'.'+identity['version']+'.nupkg'; source=base/'feed'/name
    if sha(source)!=prepared['packages'][name]: p.error('Original SDK package changed: '+name)
    shutil.copy2(source,feed/name)
config=ET.Element('configuration'); sources=ET.SubElement(config,'packageSources'); ET.SubElement(sources,'clear')
for name,value in [('local',str(feed)),('nuget.org','https://api.nuget.org/v3/index.json')]:
    ET.SubElement(sources,'add',{'key':name,'value':value})
mapping=ET.SubElement(config,'packageSourceMapping')
for name,pattern in [('local','AgentMemory.*'),('nuget.org','*')]:
    entry=ET.SubElement(mapping,'packageSource',{'key':name}); ET.SubElement(entry,'package',{'pattern':pattern})
settings=ET.SubElement(config,'config'); ET.SubElement(settings,'add',{'key':'globalPackagesFolder','value':str(out/'packages')})
ET.indent(config); ET.ElementTree(config).write(out/'NuGet.Config',encoding='utf-8',xml_declaration=True)
commands=[]
def run(name,command,allow_failure=False):
    result=subprocess.run(command,cwd=ROOT,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,encoding='utf-8',errors='replace',timeout=600)
    (out/(name+'.log')).write_text(result.stdout,encoding='utf-8')
    commands.append({'name':name,'command':command,'exitCode':result.returncode})
    (out/'commands.json').write_text(json.dumps(commands,indent=2)+'\n',encoding='utf-8')
    if result.returncode and not allow_failure: raise SystemExit(name+' failed; see retained log.')
    return result.returncode
props=['-p:ModuleSdkVersion=0.1.0','-p:CommitmentsPackageVersion=2.0.0','-p:RestoreConfigFile='+str(out/'NuGet.Config')]
run('pack-v2',['dotnet','pack','eng/module-consumers/Commitments/Commitments.csproj','-c','Release','-o',str(feed),*props])
consumer=ROOT/'eng/module-consumers/Commitments.NativeChecks/Commitments.NativeChecks.csproj'
run('build-v2-consumer',['dotnet','build',str(consumer),'-c','Release',*props])
run('trace-cold-start',['dotnet',str(consumer.parent/'bin/Release/net10.0/Commitments.NativeChecks.dll'),'--verify-trace-capture'])
assets_path=consumer.parent/'obj/project.assets.json'; assets=json.loads(assets_path.read_bytes())
if any(v['type']=='project' for v in assets['libraries'].values()): raise SystemExit('ProjectReference consumer bypass.')
(out/'consumer-assets.json').write_bytes(assets_path.read_bytes())
loaded={}
for identity in assets['libraries']:
    package,version=identity.split('/')
    if not package.startswith('AgentMemory.'): continue
    archive_path=feed/(package+'.'+version+'.nupkg')
    with zipfile.ZipFile(archive_path) as archive: data=archive.read('lib/net10.0/'+package+'.dll')
    actual=consumer.parent/'bin/Release/net10.0'/(package+'.dll')
    digest=hashlib.sha256(data).hexdigest()
    if actual.read_bytes()!=data: raise SystemExit('Unexpected v2 consumer dependency: '+package)
    if package!='AgentMemory.Examples.Commitments' and prepared['loadedPackages'][package]!={'version':version,'sha256':digest}:
        raise SystemExit('SDK dependency changed during upgrade: '+package)
    if package=='AgentMemory.Examples.Commitments' and version!='2.0.0': raise SystemExit('V2 package not loaded.')
    metadata=json.loads((out/'packages'/package.lower()/version/'.nupkg.metadata').read_bytes())
    if Path(metadata['source']).resolve()!=feed: raise SystemExit('Unexpected v2 package source: '+package)
    loaded[package]={'version':version,'sha256':digest}
if set(loaded)!=set(prepared['loadedPackages']): raise SystemExit('Dependency roster changed.')
runtime=out/'runtime-v2'; shutil.copytree(consumer.parent/'bin/Release/net10.0',runtime)
report={'createdAtUtc':datetime.now(timezone.utc).isoformat(),'worldSha256':a.world_sha256,'specSha256':a.spec_sha256,
        'primaryReceiptSha256':sha(primary_path),'packages':{f.name:sha(f) for f in feed.glob('*.nupkg')},
        'loadedPackages':loaded,'consumerSha256':sha(runtime/'Commitments.NativeChecks.dll'),'serviceExecuted':False,'status':'prepared'}
report_path=out/'package-proof.json'; report_path.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
if sha(world)!=a.world_sha256 or sha(spec)!=a.spec_sha256: raise SystemExit('Frozen input changed during build.')
code=run('native-upgrade',['dotnet',str(runtime/'Commitments.NativeChecks.dll'),'--world',str(world),'--output',str(base/'upgrade'),
    '--upgrade-from',str(primary_path),'--upgrade-spec',str(spec)],allow_failure=True)
result_path=base/'upgrade/report.json'
if result_path.exists():
    result=json.loads(result_path.read_bytes())
    if result['worldSha256']!=a.world_sha256 or result['packageVersion']!='2.0.0': raise SystemExit('Runtime identity mismatch.')
    for package,key in [('AgentMemory.Examples.Commitments','assembly'),('AgentMemory.Extensibility','sdk')]:
        if sha(result[key])!=loaded[package]['sha256']: raise SystemExit('Unexpected runtime assembly: '+package)
    trace_valid = write_verdict(base/'upgrade/telemetry-checks.json', result, json.loads(world.read_bytes()), json.loads(spec.read_bytes()))
    if not trace_valid: code = 1
    if code==0 and (result['failed'] or not result['checks'] or result['status']!='upgrade_passed_acceptance_incomplete'):
        raise SystemExit('Exit code disagrees with upgrade receipt.')
elif code==0: raise SystemExit('No upgrade receipt.')
report.update(serviceExecuted=True,status='upgrade_passed_lifecycle_pending' if code==0 else 'failed')
report_path.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'status':report['status'],'receipt':str(result_path)}))
sys.exit(code)
