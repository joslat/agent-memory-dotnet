"""Prepare independent NuGet consumers and run a supplied frozen world on explicitly disposable Neo4j."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', required=True)
parser.add_argument('--world')
parser.add_argument('--world-sha256')
parser.add_argument('--build-only', action='store_true', help='Compile the consumer without service access; not acceptance.')
args = parser.parse_args()
if not args.build_only and (not args.world or not args.world_sha256):
    parser.error('A frozen world and its pre-registered SHA256 are required for service execution.')
if not args.build_only:
    world=Path(args.world).resolve()
    if hashlib.sha256(world.read_bytes()).hexdigest()!=args.world_sha256:
        raise SystemExit('Frozen world hash mismatch; refusing preparation and service execution.')
    uri=urlparse(os.environ.get('NEO4J_URI',''))
    if os.environ.get('AGENTMEMORY_DISPOSABLE_NEO4J')!='1' or uri.scheme!='bolt' or uri.hostname not in ('localhost','127.0.0.1','::1'):
        raise SystemExit('Explicit disposable loopback Bolt service required.')
    if not os.environ.get('NEO4J_PASSWORD'): raise SystemExit('Missing disposable service password.')
out = (ROOT / args.output).resolve()
if not out.is_relative_to((ROOT / 'artifacts').resolve()):
    raise SystemExit('Output must remain under artifacts.')
out.mkdir(parents=True, exist_ok=False)
feed = out / 'feed'; feed.mkdir()
config = ET.Element('configuration'); sources = ET.SubElement(config, 'packageSources'); ET.SubElement(sources, 'clear')
ET.SubElement(sources, 'add', {'key':'local','value':str(feed)})
ET.SubElement(sources, 'add', {'key':'nuget.org','value':'https://api.nuget.org/v3/index.json'})
mapping = ET.SubElement(config,'packageSourceMapping')
local = ET.SubElement(mapping,'packageSource',{'key':'local'}); ET.SubElement(local,'package',{'pattern':'AgentMemory.*'})
external = ET.SubElement(mapping,'packageSource',{'key':'nuget.org'}); ET.SubElement(external,'package',{'pattern':'*'})
settings=ET.SubElement(config,'config'); ET.SubElement(settings,'add',{'key':'globalPackagesFolder','value':str(out/'packages')})
ET.indent(config); ET.ElementTree(config).write(out/'NuGet.Config',encoding='utf-8',xml_declaration=True)
commands=[]
def sha(data): return hashlib.sha256(data).hexdigest()
def run(name, command, allow_failure=False):
    result=subprocess.run(command,cwd=ROOT,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,encoding='utf-8',errors='replace')
    (out/(name+'.log')).write_text(result.stdout,encoding='utf-8')
    commands.append({'name':name,'command':command,'exitCode':result.returncode})
    (out/'commands.json').write_text(json.dumps(commands,indent=2)+'\n',encoding='utf-8')
    if result.returncode and not allow_failure: raise SystemExit(name+' failed; inspect retained log.')
    return result.returncode

projects=['AgentMemory.Abstractions','AgentMemory.Core','AgentMemory.Neo4j','AgentMemory.AgentFramework',
          'AgentMemory.Extensibility','AgentMemory.Extensibility.Neo4j','AgentMemory.Extensibility.AgentFramework']
for project in ['AgentMemory.Extensibility.Neo4j','AgentMemory.Extensibility.AgentFramework']:
    run('build-'+project,['dotnet','build',f'src/{project}/{project}.csproj','-c','Release'])
for project in projects:
    run('pack-'+project,['dotnet','pack',f'src/{project}/{project}.csproj','-c','Release','--no-build','--no-restore','-o',str(feed)])
props=['-p:ModuleSdkVersion=0.1.0','-p:CommitmentsPackageVersion=1.0.0','-p:RestoreConfigFile='+str(out/'NuGet.Config')]
run('pack-example',['dotnet','pack','eng/module-consumers/Commitments/Commitments.csproj','-c','Release','-o',str(feed),*props])
consumer=ROOT/'eng/module-consumers/Commitments.NativeChecks/Commitments.NativeChecks.csproj'
run('build-consumer',['dotnet','build',str(consumer),'-c','Release',*props])
assets_path=consumer.parent/'obj/project.assets.json'; assets=json.loads(assets_path.read_bytes())
if any(v['type']=='project' for v in assets['libraries'].values()): raise SystemExit('ProjectReference consumer bypass detected.')
(out/'consumer-assets.json').write_bytes(assets_path.read_bytes())
# Verify every AgentMemory binary, not only the example, against the freshly packed feed.
loaded={}
for identity in assets['libraries']:
    package_id,version=identity.split('/')
    if not package_id.startswith('AgentMemory.'): continue
    archive_path=feed/(package_id+'.'+version+'.nupkg')
    with zipfile.ZipFile(archive_path) as archive:
        data=archive.read('lib/net10.0/'+package_id+'.dll')
    actual=consumer.parent/'bin/Release/net10.0'/(package_id+'.dll')
    if actual.read_bytes()!=data: raise SystemExit('Consumer DLL differs from fresh nupkg: '+package_id)
    metadata=json.loads((out/'packages'/package_id.lower()/version/'.nupkg.metadata').read_bytes())
    if Path(metadata['source']).resolve()!=feed: raise SystemExit('Unexpected package source: '+package_id)
    loaded[package_id]={'version':version,'sha256':sha(data)}
source={p.relative_to(ROOT).as_posix():sha(p.read_bytes()) for p in (ROOT/'eng/module-consumers').rglob('*')
        if p.is_file() and 'bin' not in p.parts and 'obj' not in p.parts}
source['eng/verify-commitments-native.py']=sha(Path(__file__).read_bytes())
report={'createdAtUtc':datetime.now(timezone.utc).isoformat(),
        'head':subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),
        'worktreeStatus':subprocess.check_output(['git','status','--short'],cwd=ROOT,text=True),
        'sourceHashes':source,'packages':{p.name:sha(p.read_bytes()) for p in feed.glob('*.nupkg')},
        'loadedPackages':loaded,'serviceExecuted':False,'status':'prepared'}
(out/'package-proof.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
if args.build_only:
    print(json.dumps({'status':'build_only','packages':len(loaded),'serviceExecuted':False})); sys.exit(0)
if sha(world.read_bytes())!=args.world_sha256: raise SystemExit('Frozen world changed during preparation; refusing service execution.')
code=run('native-primary',['dotnet','run','--project',str(consumer),'-c','Release','-f','net10.0','--no-build','--no-restore',*props,
                         '--','--world',str(world),'--output',str(out/'primary')],allow_failure=True)
result_path=out/'primary/report.json'
if result_path.exists():
    result=json.loads(result_path.read_bytes())
    if result['worldSha256']!=args.world_sha256 or result['packageVersion']!='1.0.0':
        raise SystemExit('Runtime world/package identity mismatch.')
    for package_id,key in [('AgentMemory.Examples.Commitments','assembly'),('AgentMemory.Extensibility','sdk')]:
        if sha(Path(result[key]).read_bytes())!=loaded[package_id]['sha256']:
            raise SystemExit('Runtime loaded an unexpected package DLL: '+package_id)
    if code==0 and (result['failed'] or not result['checks'] or result['status']!='primary_passed_acceptance_incomplete'):
        raise SystemExit('Process exit disagrees with acceptance receipt.')
elif code==0:
    raise SystemExit('Process succeeded without an acceptance receipt.')
report['serviceExecuted']=True; report['status']='primary_passed_acceptance_incomplete' if code==0 else 'failed'
(out/'package-proof.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'status':report['status'],'serviceExecuted':True,'report':str(out/'primary/report.json')}))
sys.exit(code)
