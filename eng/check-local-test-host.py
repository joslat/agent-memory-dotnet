"""Read-only checks before local Docker-backed tests; never start tests or change Docker/WSL.

The free-space threshold is operational headroom, not a measured suite requirement or a filesystem
health guarantee. Remote daemon disk capacity must be checked on that host separately.
"""
from pathlib import Path
import argparse
import json
import os
import shutil
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--minimum-free-gib', type=float, default=10)
    parser.add_argument('--docker-timeout-seconds', type=int, default=20)
    parser.add_argument('--docker-data-path', type=Path)
    args = parser.parse_args()
    if not 1 <= args.minimum_free_gib <= 1024 or not 1 <= args.docker_timeout_seconds <= 120:
        parser.error('Disk headroom must be 1-1024 GiB and timeout 1-120 seconds.')
    try:
        workspace = Path(__file__).resolve().parent.parent
        paths = [workspace]
        if args.docker_data_path:
            paths.append(args.docker_data_path.resolve(strict=True))
        elif os.environ.get('LOCALAPPDATA'):
            default_disk = Path(os.environ['LOCALAPPDATA']) / 'Docker/wsl/disk/docker_data.vhdx'
            if default_disk.exists():
                paths.append(default_disk)
        for path in dict.fromkeys(paths):
            free_gib = shutil.disk_usage(path).free / 1024**3
            print(f'{path}: {free_gib:.2f} GiB available; threshold {args.minimum_free_gib:.2f} GiB.')
            if free_gib < args.minimum_free_gib:
                raise RuntimeError('Insufficient disk headroom. Preserve evidence and user databases when freeing space.')
        docker = shutil.which('docker')
        if not docker:
            raise RuntimeError('Docker CLI was not found.')
        # No shell, no containers, no restart. Timeout terminates only this CLI client.
        process = subprocess.run([docker, 'info', '--format', '{{json .ServerVersion}}'],
                                 capture_output=True, text=True, timeout=args.docker_timeout_seconds,
                                 creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        if process.returncode != 0 or process.stderr.strip():
            raise RuntimeError('Docker server health check failed. Inspect Docker before starting container tests.')
        # Desktop can exit 0 while printing an engine-startup error. Exit code alone is not readiness.
        try:
            version = json.loads(process.stdout)
        except json.JSONDecodeError:
            raise RuntimeError('Docker did not return a valid server-version response.') from None
        if not isinstance(version, str) or not version or not version[0].isdigit():
            raise RuntimeError('Docker returned no usable server version.')
        print(f'Docker server {version} responded. No containers were created.')
        return 0
    except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
        print(f'Local service-test preflight refused: {error}', file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
