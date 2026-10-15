"""Run frozen negative forks against separate disposable hosted containers using one verified package build."""
import argparse
from datetime import datetime,timezone
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import time

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--prepared',required=True)
parser.add_argument('--world',required=True)
parser.add_argument('--world-sha256',required=True)
args=parser.parse_args()
if os.environ.get('GITHUB_ACTIONS')!='true': raise SystemExit('Container orchestration is hosted-only; local Docker is not used.')
proof_path=Path(args.prepared).resolve(); prepared=json.loads(proof_path.read_bytes())
if prepared['status']!='primary_passed_acceptance_incomplete' or not prepared['serviceExecuted']:
    raise SystemExit('A successful primary-world receipt is required before negative forks.')
world_path=Path(args.world).resolve(); data=world_path.read_bytes()
if hashlib.sha256(data).hexdigest()!=args.world_sha256: raise SystemExit('Frozen world hash mismatch.')
world=json.loads(data); cases=[c['id'] for c in world['negativeCases']]
if len(cases)!=len(set(cases)) or not all(re.fullmatch(r'[a-z][a-z0-9-]{0,63}',c) for c in cases):
    raise SystemExit('Invalid or duplicate case identities.')
consumer=Path(prepared['consumerAssembly']['path'])
if hashlib.sha256(consumer.read_bytes()).hexdigest()!=prepared['consumerAssembly']['sha256']:
    raise SystemExit('Consumer binary differs from the prepared build.')
service=os.environ.get('NEO4J_SERVICE_ID','')
if not re.fullmatch(r'[0-9a-f]{64}',service): raise SystemExit('Exact primary service container identity is required.')
original=json.loads(subprocess.check_output(['docker','inspect',service]))[0]
image=original['Image']
if not re.fullmatch(r'sha256:[0-9a-f]{64}',image): raise SystemExit('Cannot bind the original service image.')
root=proof_path.parent/'negative'; root.mkdir(exist_ok=False)
(root/'primary-service.json').write_text(json.dumps(original,indent=2)+'\n',encoding='utf-8')
results=[]
for case in cases:
    folder=root/case;folder.mkdir()
    nonce=secrets.token_hex(8); name='commitments-'+case+'-'+nonce
    container=None; started=time.monotonic(); result={'case':case,'status':'failed','startedAtUtc':datetime.now(timezone.utc).isoformat()}
    try:
        password=secrets.token_urlsafe(20)
        command=['docker','run','--detach','--name',name,'--label','agentmemory.validation='+nonce,
            '--memory','2g','--cpus','2','--publish','127.0.0.1::7687',
            '--env','NEO4J_AUTH=neo4j/'+password,'--env','NEO4J_server_memory_heap_initial__size=256M',
            '--env','NEO4J_server_memory_heap_max__size=512M','--env','NEO4J_server_memory_pagecache_size=128M',image]
        container=subprocess.check_output(command,text=True).strip()
        if not re.fullmatch(r'[0-9a-f]{64}',container): raise RuntimeError('Unexpected created container identity.')
        for attempt in range(40):
            ready=subprocess.run(['docker','exec',container,'cypher-shell','-u','neo4j','-p',password,'RETURN 1'],
                stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,timeout=15)
            if ready.returncode==0: break
            time.sleep(3)
        else: raise RuntimeError('Disposable Neo4j did not become ready.')
        port=subprocess.check_output(['docker','port',container,'7687/tcp'],text=True).strip()
        if not re.fullmatch(r'127\.0\.0\.1:[0-9]+',port): raise RuntimeError('Service was not bound exclusively to loopback.')
        env=dict(os.environ);env.update(NEO4J_URI='bolt://'+port,NEO4J_PASSWORD=password,AGENTMEMORY_DISPOSABLE_NEO4J='1')
        run=subprocess.run(['dotnet',str(consumer),'--world',str(world_path),'--output',str(folder/'receipt'),'--case',case],
            env=env,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,encoding='utf-8',errors='replace',timeout=180)
        (folder/'consumer.log').write_text(run.stdout,encoding='utf-8')
        receipt_path=folder/'receipt/report.json'
        if not receipt_path.exists(): raise RuntimeError('Consumer exited without a receipt.')
        receipt=json.loads(receipt_path.read_bytes())
        result.update(exitCode=run.returncode,passed=receipt['passed'],failed=receipt['failed'])
        if receipt['worldSha256']!=args.world_sha256 or receipt['scenario']!=case:
            raise RuntimeError('Wrong frozen world or scenario in receipt.')
        for package,key in [('AgentMemory.Examples.Commitments','assembly'),('AgentMemory.Extensibility','sdk')]:
            if hashlib.sha256(Path(receipt[key]).read_bytes()).hexdigest()!=prepared['loadedPackages'][package]['sha256']:
                raise RuntimeError('Unexpected runtime package identity.')
        if run.returncode==0 and receipt['status']=='negative_passed_acceptance_incomplete' and receipt['failed']==0:
            result['status']='passed'
    except Exception as error:
        result['error']=str(error)
    finally:
        if container and re.fullmatch(r'[0-9a-f]{64}',container):
            # Remove only the container created by this exact iteration, after checking its nonce.
            inspected=json.loads(subprocess.check_output(['docker','inspect',container]))[0]
            (folder/'container.json').write_text(json.dumps(inspected,indent=2)+'\n',encoding='utf-8')
            logs=subprocess.run(['docker','logs',container],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
            (folder/'container.log').write_bytes(logs.stdout)
            if inspected['Config']['Labels'].get('agentmemory.validation')!=nonce:
                raise RuntimeError('Refusing cleanup: container ownership label changed.')
            subprocess.run(['docker','rm','--force',container],check=True,stdout=subprocess.PIPE)
        result['durationSeconds']=round(time.monotonic()-started,3);results.append(result)
        (root/'summary.json').write_text(json.dumps({'worldSha256':args.world_sha256,'imageId':image,'results':results,
            'expectedCases':cases,'complete':len(results)==len(cases),'acceptanceComplete':False},indent=2)+'\n',encoding='utf-8')
    print(json.dumps(result),flush=True)
    if 'error' in result: break  # An infrastructure/identity failure is not twelve independent semantic failures.
raise SystemExit(0 if len(results)==len(cases) and all(r['status']=='passed' for r in results) else 1)
