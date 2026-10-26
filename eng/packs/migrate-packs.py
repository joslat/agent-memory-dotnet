"""PACK-migrate: every existing pack as a signed v1.2 memory pack, derived from its current source.

The sources stay where they are (the knowledge bundle under packs/, the module manifests under src/), so the old and new
forms cannot drift apart: this script derives the .ampack files and an old-to-new digest map each time it runs.

usage: python eng/packs/migrate-packs.py <ampack executable> <publisher private key PEM> [<out folder>]
Writes <out>/<id>-<version>.ampack, <out>/trust.json (the public key, scoped to io.agentmemory.packs) and
<out>/digest-map.json. Default out: artifacts/packs.
"""
import hashlib
import json
import shutil
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
AMPACK, KEY = str(Path(sys.argv[1]).resolve()), Path(sys.argv[2]).resolve()
OUT = Path(sys.argv[3]) if len(sys.argv) > 3 else REPO / "artifacts" / "packs"
SOURCES = [
    ("knowledge", REPO / "packs/AgentMemory.Packs.MartialArts/agentmemory"),
    ("definition", REPO / "src/AgentMemory.Packs.CodeMap/manifest.json"),
    ("definition", REPO / "src/AgentMemory.Packs.Decisions/manifest.json"),
    ("definition", REPO / "src/AgentMemory.Packs.Work/manifest.json"),
    ("definition", REPO / "src/AgentMemory.Packs.Repository/manifest.json"),
]


def run(*args):
    r = subprocess.run([AMPACK, *map(str, args)], capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0:
        raise SystemExit(f"ampack {' '.join(map(str, args))} failed:\n{r.stdout}{r.stderr}")
    return r.stdout


def main():
    if OUT.exists():
        shutil.rmtree(OUT)
    work = OUT / "src"
    work.mkdir(parents=True)
    rows = []
    for kind, source in SOURCES:
        manifest = json.loads((source / "manifest.json" if source.is_dir() else source).read_text(encoding="utf-8-sig"))
        pid, version = manifest["id"], manifest["version"]
        old = None
        if source.is_dir() and (source / "agentmemory.module.files.json").exists():
            old = "sha256:" + hashlib.sha256((source / "agentmemory.module.files.json").read_bytes()).hexdigest()
        folder = work / pid
        run("migrate", source, "--out", folder)
        archive = OUT / f"{pid}-{version}.ampack"
        text = run("build", folder, "--key", KEY, "--out", archive)
        new = next(line.split()[-1] for line in text.splitlines() if line.strip().startswith("digest"))
        rows.append({"id": pid, "version": version, "kind": kind, "source": str(source.relative_to(REPO)).replace("\\", "/"),
                     "oldDigest": old, "newDigest": new, "file": archive.name})
        print(f"{pid:36} {version:16} {kind:10} {new}")
    public = KEY.with_name(KEY.name.replace(".key.pem", ".pub.pem")).read_text(encoding="utf-8")
    key_id = hashlib.sha256(__import__("base64").b64decode("".join(l for l in public.splitlines() if "-----" not in l))).hexdigest()
    (OUT / "trust.json").write_bytes((json.dumps({"format": "agentmemory.pack.trust/1.1", "keys": [
        {"keyId": key_id, "publicKey": public, "idPrefixes": ["io.agentmemory.packs"], "roles": ["publisher"]}]}, indent=1) + "\n").encode("utf-8"))
    for row in rows:
        run("verify", OUT / row["file"], "--policy", OUT / "trust.json")
    (OUT / "digest-map.json").write_bytes((json.dumps({"format": "agentmemory.pack.migration/1.2", "packs": rows}, indent=1) + "\n").encode("utf-8"))
    shutil.rmtree(work)
    print(f"{len(rows)} packs migrated and verified; digest map in {OUT / 'digest-map.json'}")


if __name__ == "__main__":
    main()
