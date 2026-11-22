"""NATIVE-TS: installs the packed npm tarballs into an empty project, as a user would, and checks what they do.

1. The main package and this platform's package install; the binding loads the library from the platform package
   (no AGENTMEMORY_NATIVE_LIBRARY), checked against its MANIFEST.json, and opens a context (tier A: no store).
2. A damaged library in the installed platform package is refused before it is loaded.
3. The main package without its platform package (as when npm could not install it) fails with the error that names
   the fix. Not --omit=optional: koffi's own prebuilt binaries are optional dependencies too, so that install fails in
   koffi's build from source before this binding runs (documented in native/typescript/README.md).

    python npm_install_test.py --main out/npm/agentmemory-native-0.2.0.tgz --platform out/npm/agentmemory-native-win32-x64-0.2.0.tgz
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

CHECK = r"""
import { MemoryClient, resolveLibrary, NativeLibraryError } from "agentmemory-native";
const mode = process.argv[2];
if (mode === "omit") {
  try { new MemoryClient({}); console.log(JSON.stringify({ loaded: true })); }
  catch (e) { console.log(JSON.stringify({ loaded: false, library: e instanceof NativeLibraryError, message: e.message })); }
} else {
  let resolved, opened = null, error = null;
  try {
    resolved = resolveLibrary();
    const memory = new MemoryClient({ schemaVersion: 1, profile: "read_only", owner: { mode: "fixed", id: "o" },
      store: { uri: "bolt://127.0.0.1:9", username: "neo4j", password: "x", verifyConnectivity: false },
      embedding: { dimensions: 8, model: "m" } });
    const caps = await memory.capabilities();
    opened = { status: caps.status, abi: caps.abi, handleType: typeof memory.handle };
    memory.close();
  } catch (e) { error = { name: e.name, message: e.message }; }
  console.log(JSON.stringify({ path: resolved?.path, packaged: resolved?.manifest != null, platformTag: resolved?.manifest?.platformTag, opened, error }));
}
"""


def npm() -> str:
    return shutil.which("npm") or "npm"


def project(tarballs: list[Path], *flags: str) -> Path:
    folder = Path(tempfile.mkdtemp(prefix="am-npm-"))
    (folder / "package.json").write_text(json.dumps({"name": "am-install-test", "version": "1.0.0", "private": True, "type": "module"}), encoding="utf-8")
    done = subprocess.run([npm(), "install", "--no-audit", "--no-fund", *flags, *[str(t.resolve()) for t in tarballs]],
                          cwd=folder, capture_output=True, text=True, shell=os.name == "nt")
    print(done.stdout[-2000:], done.stderr[-2000:], sep="\n", flush=True)
    if done.returncode != 0:
        sys.exit(f"npm install failed ({done.returncode})")
    (folder / "check.mjs").write_text(CHECK, encoding="utf-8")
    return folder


def check(folder: Path, mode: str) -> dict:
    env = {k: v for k, v in os.environ.items() if k != "AGENTMEMORY_NATIVE_LIBRARY"}
    done = subprocess.run(["node", "check.mjs", mode], cwd=folder, capture_output=True, text=True, env=env)
    if done.returncode != 0:
        sys.exit(f"node check.mjs {mode} failed ({done.returncode}): {done.stderr[-2000:]}")
    return json.loads(done.stdout.strip().splitlines()[-1])


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--main", type=Path, required=True)
    parser.add_argument("--platform", type=Path, required=True)
    args = parser.parse_args()
    failures = []

    installed = project([args.main, args.platform])
    first = check(installed, "load")
    print("installed:", first, flush=True)
    if not (first["packaged"] and first["opened"] and first["opened"]["status"] == "ok" and first["opened"]["handleType"] == "bigint"):
        failures.append(f"the installed package did not load its packaged library and open: {first}")

    # A second install, damaged before anything loads it: Windows keeps a loaded DLL from being rewritten for a while.
    second = project([args.main, args.platform])
    library = next((second / "node_modules").glob("agentmemory-native-*/_native/agentmemory_native.*"), None)
    if library and library.is_file():
        installed = second
        data = bytearray(library.read_bytes())
        data[len(data) // 2] ^= 0xFF
        library.write_bytes(bytes(data))
        damaged = check(installed, "load")
        print("damaged:", damaged, flush=True)
        if not (damaged["error"] and "reinstall agentmemory-native" in damaged["error"]["message"]):
            failures.append(f"a damaged packaged library was not refused: {damaged}")

    # The platform packages are not on the registry (not published), so npm skips them as optional dependencies.
    omitted = check(project([args.main]), "omit")
    print("without the platform package:", omitted, flush=True)
    if omitted.get("loaded") or not omitted.get("library") or "--omit=optional" not in omitted.get("message", ""):
        failures.append(f"a missing platform package did not give the error that names the fix: {omitted}")

    for failure in failures:
        print("FAIL", failure)
    print("npm install test:", "passed" if not failures else f"{len(failures)} failed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
