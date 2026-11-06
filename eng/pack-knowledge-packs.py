"""Signs and packs every knowledge pack under packs/ (Extensibility 0.13 §K6, §K7).

    python eng/pack-knowledge-packs.py --key <private key PEM> --output artifacts/packs [--only <PackageId>]

Each bundle (packs/<PackageId>/agentmemory) gets its files list and signature from `agentmemory packs sign`, is verified
with the matching public key, and is packed as a NuGet package of type AgentMemoryKnowledgePack. The release signs with
the official key; local runs sign with a developer key. Signed files are not committed (.gitignore).
"""
import argparse, pathlib, subprocess, sys, tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent


def run(command):
    print('>', ' '.join(str(c) for c in command), flush=True)
    result = subprocess.run([str(c) for c in command], cwd=ROOT)
    if result.returncode != 0:
        sys.exit(f'failed ({result.returncode}): {command[0]}')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--key', required=True, help='Private signing key (PEM). Never commit it.')
    parser.add_argument('--output', default='artifacts/packs', help='Folder for the .nupkg files (under artifacts/).')
    parser.add_argument('--only', help='Pack only this PackageId.')
    args = parser.parse_args()
    output = (ROOT / args.output).resolve()
    if ROOT / 'artifacts' not in output.parents and output != ROOT / 'artifacts':
        sys.exit('--output must be under artifacts/.')
    key = pathlib.Path(args.key).resolve()
    if not key.is_file():
        sys.exit(f'No key at {key}.')

    run(['dotnet', 'build', 'tools/AgentMemory.Cli', '-c', 'Release', '-m:2', '-nologo', '-v:q'])
    cli = ROOT / 'tools/AgentMemory.Cli/bin/Release/net10.0/AgentMemory.Cli.dll'
    projects = sorted(p for p in (ROOT / 'packs').glob('*/*.csproj') if (p.parent / 'agentmemory/manifest.json').is_file())
    if args.only:
        projects = [p for p in projects if p.stem == args.only]
    if not projects:
        sys.exit('No knowledge packs to pack.')
    with tempfile.TemporaryDirectory() as scratch:
        public = pathlib.Path(scratch) / 'public.pem'
        # The public half of the key, so each bundle is verified exactly as a host would before it is packed.
        run(['dotnet', cli, 'packs', 'public-key', '--key', key, '--out', public])
        for project in projects:
            bundle = project.parent / 'agentmemory'
            run(['dotnet', cli, 'packs', 'sign', bundle, '--key', key])
            run(['dotnet', cli, 'packs', 'verify', bundle, '--developer-key', public])
            run(['dotnet', 'pack', project, '-c', 'Release', '-o', output, '-nologo', '-v:q'])
    print(f'packed {len(projects)} knowledge pack(s) into {output}')


if __name__ == '__main__':
    main()
