"""PACK-migrate and PACK-composite: every existing pack as a signed v1.2 memory pack, derived from its current source.

The sources stay where they are (the knowledge bundle under packs/, the module and composite manifests under src/), so
the old and new forms cannot drift apart: this script derives the .ampack files and an old-to-new digest map each time.

It writes:
- one .ampack per pack: the martial-arts knowledge pack; the code map, decisions, work and repository definitions;
- the Coding composite, its lock pinning those four members' exact digests;
- the Coding family as one pack set: the composite and its four members in one signed archive;
- trust.json (the public key, scoped to io.agentmemory.packs) and digest-map.json.

usage: python eng/packs/migrate-packs.py <ampack executable> <publisher private key PEM> [<out folder>]
Default out: artifacts/packs.
"""
import base64
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
COMPOSITE = REPO / "src/AgentMemory.Packs.Coding/composite.json"
FAMILY_ID = "io.agentmemory.packs.coding-family"


def run(*args):
    r = subprocess.run([AMPACK, *map(str, args)], capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0:
        raise SystemExit(f"ampack {' '.join(map(str, args))} failed:\n{r.stdout}{r.stderr}")
    return r.stdout


def digest_of(text):
    return next(line.split()[-1] for line in text.splitlines() if line.strip().startswith("digest"))


def write_lf(path, value):
    path.write_bytes((json.dumps(value, indent=1) + "\n").encode("utf-8"))


def main():
    if OUT.exists():
        shutil.rmtree(OUT)
    work = OUT / "src"
    work.mkdir(parents=True)
    rows, built = [], {}
    for kind, source in SOURCES:
        manifest = json.loads((source / "manifest.json" if source.is_dir() else source).read_text(encoding="utf-8-sig"))
        pid, version = manifest["id"], manifest["version"]
        old = None
        if source.is_dir() and (source / "agentmemory.module.files.json").exists():
            old = "sha256:" + hashlib.sha256((source / "agentmemory.module.files.json").read_bytes()).hexdigest()
        folder = work / pid
        run("migrate", source, "--out", folder)
        archive = OUT / f"{pid}-{version}.ampack"
        new = digest_of(run("build", folder, "--key", KEY, "--out", archive))
        built[pid] = archive
        rows.append({"id": pid, "version": version, "kind": kind, "source": str(source.relative_to(REPO)).replace("\\", "/"),
                     "oldDigest": old, "newDigest": new, "file": archive.name})
        print(f"{pid:36} {version:16} {kind:10} {new}")

    # The Coding composite: its members pinned by the digests just built.
    composite = json.loads(COMPOSITE.read_text(encoding="utf-8-sig"))
    cid, cversion = composite["id"], composite["version"]
    folder = work / cid
    run("migrate", COMPOSITE, "--out", folder)
    run("lock", folder, *[built[m["id"]] for m in composite["members"]])
    archive = OUT / f"{cid}-{cversion}.ampack"
    new = digest_of(run("build", folder, "--key", KEY, "--out", archive))
    rows.append({"id": cid, "version": cversion, "kind": "composite", "source": str(COMPOSITE.relative_to(REPO)).replace("\\", "/"),
                 "oldDigest": None, "newDigest": new, "file": archive.name})
    print(f"{cid:36} {cversion:16} {'composite':10} {new}")

    # The family as one pack set.
    family = OUT / f"{FAMILY_ID}-{cversion}.ampack"
    new = digest_of(run("set", family, archive, *[built[m["id"]] for m in composite["members"]],
                        "--id", FAMILY_ID, "--version", cversion, "--key", KEY))
    rows.append({"id": FAMILY_ID, "version": cversion, "kind": "set", "source": "the composite and its members",
                 "oldDigest": None, "newDigest": new, "file": family.name})
    print(f"{FAMILY_ID:36} {cversion:16} {'set':10} {new}")

    public = KEY.with_name(KEY.name.replace(".key.pem", ".pub.pem")).read_text(encoding="utf-8")
    key_id = hashlib.sha256(base64.b64decode("".join(l for l in public.splitlines() if "-----" not in l))).hexdigest()
    write_lf(OUT / "trust.json", {"format": "agentmemory.pack.trust/1.1", "keys": [
        {"keyId": key_id, "publicKey": public, "idPrefixes": ["io.agentmemory.packs"], "roles": ["publisher"]}]})
    for row in rows:
        run("verify", OUT / row["file"], "--policy", OUT / "trust.json")
    write_lf(OUT / "digest-map.json", {"format": "agentmemory.pack.migration/1.2", "packs": rows})
    shutil.rmtree(work)
    print(f"{len(rows)} packs built and verified; digest map in {OUT / 'digest-map.json'}")


if __name__ == "__main__":
    main()
