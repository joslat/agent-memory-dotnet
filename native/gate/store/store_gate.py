"""The gate's store tiers on any platform (NATIVE-PLAT-STORE).

Tier B is the read gate (native/gate/tests/test_gate.py) against a seeded store, with a fingerprint of the whole graph
taken before and after: a read-only run must leave it unchanged. Tier C is the write tests (native/gate/writetests) on a
store of their own, because they write. Tier S (NATIVE-SCHEMA) is the admin profile on a fresh, unseeded store: the
library bootstraps it from Python with no .NET, then bootstraps it again, and the graph must not change between the two. Both use a local Neo4j 5.26 from the community distribution and Java 21, so one
script serves Linux, macOS and Windows (macOS runners have no Docker). On Linux the Docker scripts beside it
(run-gate.sh, run-write-gate.sh) stay the gate on the target image.

Usage:
  python store_gate.py tls <dir>
      A throw-away certificate authority (ca.crt) and a server certificate for the name "localhost" only, signed by it
      (bolt/public.crt, bolt/private.key). Trusting ca.crt is left to the caller: it changes the machine's trust store.
  python store_gate.py run (--library <file> | --wheel <file>) --work <dir> --report <dir> [--tls <dir>] [--tiers B,C]
      With --library, this Python runs the tests against the library by path. With --wheel, the wheel is installed into
      a fresh virtual environment and the tests run there against the installed package, with no path overrides: the
      way a user gets it. With --tls, the read store also accepts TLS, and the read gate checks a recall over bolt+s://
      with the certificate validated, and that a certificate for another name is refused.

Needs Java 21 (JAVA_HOME or java on PATH), the .NET SDK (the seed runs the managed library), openssl (tls only) and
Python 3.12. Standard library only. Passwords are random, never printed, and reach the tools through environment
variables, except neo4j-admin's set-initial-password, which takes it as an argument: run this on a single-user machine.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import tarfile
import time
import urllib.request
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
GATE = REPO / "native" / "gate"
WINDOWS = os.name == "nt"

NEO4J_VERSION = "5.26.33"
# The distribution's own checksums (https://dist.neo4j.org/<file>.sha256, read 22 Nov 2026), pinned so a changed file is refused.
NEO4J_SHA256 = {
    "unix.tar.gz": "db8f7ab2eab05109c87d8bb2096e14100532e5701a76d56257f06301569b6c26",
    "windows.zip": "380a4cfd26ea23ece35922ed1ab27c33503eebda634e4e58cdd6f908bd0166ff",
}
READ_PORT, WRITE_PORT, SCHEMA_PORT = 7697, 7698, 7699

FINGERPRINT = """MATCH (n) WITH n ORDER BY elementId(n) RETURN elementId(n), labels(n), properties(n);
MATCH ()-[r]->() WITH r ORDER BY elementId(r) RETURN elementId(r), type(r), properties(r);
"""


def log(message: str) -> None:
    print(f"[store-gate] {message}", flush=True)


def tool(home: Path, name: str) -> str:
    return str(home / "bin" / (f"{name}.bat" if WINDOWS else name))


def java_env() -> dict:
    env = dict(os.environ)
    if "JAVA_HOME" in env:
        # cypher-shell's Windows script runs the java on PATH, not JAVA_HOME's.
        env["PATH"] = str(Path(env["JAVA_HOME"]) / "bin") + os.pathsep + env.get("PATH", "")
    elif shutil.which("java") is None:
        sys.exit("Java 21 is needed: set JAVA_HOME or put java on PATH")
    return env


# --- certificates ---------------------------------------------------------------------------------------------------

CA_CONFIG = """[req]
distinguished_name = dn
prompt = no
[dn]
CN = AgentMemory gate test CA
[v3_ca]
basicConstraints = critical, CA:TRUE
keyUsage = critical, keyCertSign, cRLSign
subjectKeyIdentifier = hash
"""

SERVER_CONFIG = """[req]
distinguished_name = dn
prompt = no
[dn]
CN = localhost
[v3_server]
basicConstraints = critical, CA:FALSE
keyUsage = critical, digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth
subjectAltName = DNS:localhost
authorityKeyIdentifier = keyid
"""


def make_tls(folder: Path) -> None:
    openssl = shutil.which("openssl")
    if openssl is None:
        sys.exit("openssl is needed for the certificates")
    folder.mkdir(parents=True, exist_ok=True)
    bolt = folder / "bolt"
    bolt.mkdir(exist_ok=True)
    (folder / "ca.cnf").write_text(CA_CONFIG, encoding="ascii")
    (folder / "server.cnf").write_text(SERVER_CONFIG, encoding="ascii")
    env = dict(os.environ, MSYS_NO_PATHCONV="1")

    def run(*args: str) -> None:
        subprocess.run([openssl, *args], check=True, cwd=folder, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)

    run("req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", "ca.key", "-out", "ca.crt", "-days", "3",
        "-config", "ca.cnf", "-extensions", "v3_ca")
    run("req", "-new", "-newkey", "rsa:2048", "-nodes", "-keyout", "bolt/private.key", "-out", "server.csr",
        "-config", "server.cnf")
    run("x509", "-req", "-in", "server.csr", "-CA", "ca.crt", "-CAkey", "ca.key", "-set_serial", "2", "-days", "3",
        "-out", "bolt/public.crt", "-extfile", "server.cnf", "-extensions", "v3_server")
    # The authority's key is not needed again: nothing else can be signed with it.
    (folder / "ca.key").unlink()
    (folder / "server.csr").unlink()
    log(f"certificates in {folder}: ca.crt (trust it), bolt/public.crt for DNS:localhost only")


# --- Neo4j ----------------------------------------------------------------------------------------------------------

def neo4j_archive(work: Path) -> Path:
    kind = "windows.zip" if WINDOWS else "unix.tar.gz"
    name = f"neo4j-community-{NEO4J_VERSION}-{kind}"
    archive = work / name
    if not archive.exists():
        log(f"downloading {name}")
        partial = archive.with_suffix(archive.suffix + ".part")
        with urllib.request.urlopen(f"https://dist.neo4j.org/{name}", timeout=300) as answer, open(partial, "wb") as out:
            shutil.copyfileobj(answer, out, 1 << 20)
        partial.replace(archive)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    if digest != NEO4J_SHA256[kind]:
        archive.unlink()
        sys.exit(f"{name}: SHA-256 {digest} is not the pinned {NEO4J_SHA256[kind]}")
    return archive


def extract(archive: Path, into: Path) -> Path:
    if into.exists():
        shutil.rmtree(into)
    into.mkdir(parents=True)
    if archive.name.endswith(".zip"):
        with zipfile.ZipFile(archive) as z:
            z.extractall(into)
    else:
        with tarfile.open(archive) as t:
            t.extractall(into, filter="tar")
    (home,) = [p for p in into.iterdir() if p.is_dir()]
    return home


def set_settings(conf: Path, settings: dict[str, str]) -> None:
    lines = conf.read_text(encoding="utf-8").splitlines()
    kept = [line for line in lines if line.split("=", 1)[0].strip() not in settings]
    kept.append("")
    kept.append("# store_gate.py")
    kept.extend(f"{key}={value}" for key, value in settings.items())
    conf.write_text("\n".join(kept) + "\n", encoding="utf-8")


class Neo4j:
    """One local Neo4j: extracted, configured, given a random password, started in the console and stopped again."""

    def __init__(self, archive: Path, folder: Path, port: int, tls: Path | None, report: Path, name: str):
        self.home = extract(archive, folder)
        self.port = port
        self.name = name
        self.password = secrets.token_hex(16)
        self.log_path = report / f"neo4j-{name}.log"
        self.process: subprocess.Popen | None = None
        settings = {
            "server.default_listen_address": "127.0.0.1",
            "server.bolt.listen_address": f"127.0.0.1:{port}",
            "server.http.enabled": "false",
            "server.https.enabled": "false",
            "server.memory.heap.initial_size": "512m",
            "server.memory.heap.max_size": "1G",
            "server.memory.pagecache.size": "256M",
            "dbms.usage_report.enabled": "false",
        }
        if tls is not None:
            bolt = self.home / "certificates" / "bolt"
            bolt.mkdir(parents=True, exist_ok=True)
            for item in ("public.crt", "private.key"):
                shutil.copyfile(tls / "bolt" / item, bolt / item)
            for sub in ("trusted", "revoked"):
                (bolt / sub).mkdir(exist_ok=True)
            settings.update({
                "dbms.ssl.policy.bolt.enabled": "true",
                "dbms.ssl.policy.bolt.base_directory": "certificates/bolt",
                "dbms.ssl.policy.bolt.private_key": "private.key",
                "dbms.ssl.policy.bolt.public_certificate": "public.crt",
                "dbms.ssl.policy.bolt.client_auth": "NONE",
                # Plain bolt:// for the gate and bolt+s:// for its TLS cases, on the one store.
                "server.bolt.tls_level": "OPTIONAL",
            })
        set_settings(self.home / "conf" / "neo4j.conf", settings)
        subprocess.run([tool(self.home, "neo4j-admin"), "dbms", "set-initial-password", self.password], check=True,
                       env=java_env(), stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)

    @property
    def uri(self) -> str:
        return f"bolt://127.0.0.1:{self.port}"

    def start(self, timeout_s: float = 180) -> None:
        out = open(self.log_path, "wb")
        flags = {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP} if WINDOWS else {"start_new_session": True}
        self.process = subprocess.Popen([tool(self.home, "neo4j"), "console"], cwd=self.home, env=java_env(),
                                        stdout=out, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, **flags)
        started = time.monotonic()
        deadline = started + timeout_s
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                sys.exit(f"Neo4j ({self.name}) exited with {self.process.returncode}; see {self.log_path}")
            if b"Started." in self.log_path.read_bytes() and self.answers():
                log(f"Neo4j {NEO4J_VERSION} ({self.name}) ready on {self.uri} in {time.monotonic() - started:.1f} s")
                return
            time.sleep(1)
        self.stop()
        sys.exit(f"Neo4j ({self.name}) did not start in {timeout_s:.0f} s; see {self.log_path}")

    def answers(self) -> bool:
        try:
            with socket.create_connection(("127.0.0.1", self.port), timeout=2):
                return True
        except OSError:
            return False

    def stop(self) -> None:
        if self.process is None or self.process.poll() is not None:
            return
        if WINDOWS:
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(self.process.pid)], stdout=subprocess.DEVNULL,
                           stderr=subprocess.DEVNULL)
        else:
            os.killpg(self.process.pid, signal.SIGTERM)
        try:
            self.process.wait(60)
        except subprocess.TimeoutExpired:
            if not WINDOWS:
                os.killpg(self.process.pid, signal.SIGKILL)
            self.process.wait(30)
        log(f"Neo4j ({self.name}) stopped")

    def seed(self, report: Path) -> None:
        env = dict(os.environ, AM_GATE_PW=self.password, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
        with open(report / f"seed-{self.name}.log", "wb") as out:
            done = subprocess.run(["dotnet", "run", str(GATE / "seed" / "seed.cs"), "--", self.uri, "neo4j", "AM_GATE_PW"],
                                  env=env, stdout=out, stderr=subprocess.STDOUT, cwd=REPO)
        if done.returncode != 0:
            sys.exit(f"the seed failed ({done.returncode}); see {report / f'seed-{self.name}.log'}")
        log(f"seeded {self.name}: {(report / f'seed-{self.name}.log').read_text(encoding='utf-8', errors='replace').count(' owner=')} rows")

    def fingerprint(self, work: Path) -> tuple[str, int]:
        query = work / "fingerprint.cypher"
        query.write_text(FINGERPRINT, encoding="ascii")
        env = dict(java_env(), NEO4J_USERNAME="neo4j", NEO4J_PASSWORD=self.password)
        done = subprocess.run([tool(self.home, "cypher-shell"), "-a", self.uri, "--format", "plain", "--file", str(query)],
                              env=env, capture_output=True, check=False)
        if done.returncode != 0:
            sys.exit(f"cypher-shell failed ({done.returncode}): {done.stderr.decode(errors='replace')[-2000:]}")
        return hashlib.sha256(done.stdout).hexdigest(), done.stdout.count(b"\n")


# --- the tiers ------------------------------------------------------------------------------------------------------

class Runtime:
    """Where the tests run: this Python with the library by path, or a fresh virtual environment with the wheel installed."""

    def __init__(self, python: str, env: dict, mode: str, artifact: Path):
        self.python, self.env, self.mode, self.artifact = python, env, mode, artifact

    @staticmethod
    def library(library: Path) -> "Runtime":
        env = dict(os.environ, PYTHONPATH=str(REPO / "native" / "python"), AGENTMEMORY_NATIVE_LIBRARY=str(library))
        return Runtime(sys.executable, env, "library", library)

    @staticmethod
    def wheel(wheel: Path, work: Path) -> "Runtime":
        venv = work / "venv"
        if venv.exists():
            shutil.rmtree(venv)
        subprocess.run([sys.executable, "-m", "venv", str(venv)], check=True)
        python = str(venv / ("Scripts/python.exe" if WINDOWS else "bin/python"))
        subprocess.run([python, "-m", "pip", "install", "--quiet", "--no-index", "--disable-pip-version-check", str(wheel)],
                       check=True)
        env = {k: v for k, v in os.environ.items() if k not in ("PYTHONPATH", "AGENTMEMORY_NATIVE_LIBRARY")}
        log(f"installed {wheel.name} into {venv}")
        return Runtime(python, env, "wheel", wheel)


def unittest_run(runtime: Runtime, pattern_dir: Path, pattern: str, env: dict, log_path: Path) -> tuple[int, str]:
    command = [runtime.python, "-m", "unittest", "discover", "-v", "--durations", "8",
               "-s", str(pattern_dir), "-t", str(pattern_dir), "-p", pattern]
    started = time.monotonic()
    with open(log_path, "wb") as out:
        code = subprocess.run(command, env=env, stdout=out, stderr=subprocess.STDOUT, cwd=REPO).returncode
    text = log_path.read_text(encoding="utf-8", errors="replace")
    sys.stdout.write(text)
    tail = [line for line in text.splitlines() if line.startswith(("Ran ", "OK", "FAILED", "METRIC"))]
    return code, f"{' '.join(tail)} ({time.monotonic() - started:.1f} s)"


def gate_env(runtime: Runtime, store: Neo4j) -> dict:
    return dict(runtime.env, AMP_URI=store.uri, AMP_USER="neo4j", AMP_PASSWORD=store.password, PYTHONDONTWRITEBYTECODE="1")


def tier_b(archive: Path, runtime: Runtime, work: Path, report: Path, tls: Path | None) -> dict:
    store = Neo4j(archive, work / "neo4j-read", READ_PORT, tls, report, "read")
    store.start()
    try:
        store.seed(report)
        before, rows = store.fingerprint(work)
        env = gate_env(runtime, store) | {"GATE_TIER": "B"}
        if tls is not None:
            env |= {"AMP_TLS_URI": f"bolt+s://localhost:{store.port}", "AMP_TLS_OTHER_NAME_URI": f"bolt+s://127.0.0.1:{store.port}"}
        code, summary = unittest_run(runtime, GATE / "tests", "test_gate.py", env, report / f"tier-b-{runtime.mode}.log")
        after, _ = store.fingerprint(work)
    finally:
        store.stop()
    return {"tier": "B", "mode": runtime.mode, "exit": code, "summary": summary, "graphRows": rows,
            "graph": "unchanged" if before == after else "CHANGED", "before": before, "after": after, "tls": tls is not None}


def tier_c(archive: Path, runtime: Runtime, work: Path, report: Path) -> dict:
    store = Neo4j(archive, work / "neo4j-write", WRITE_PORT, None, report, "write")
    store.start()
    try:
        store.seed(report)
        code, summary = unittest_run(runtime, GATE / "writetests", "test_writes.py", gate_env(runtime, store),
                                     report / f"tier-c-{runtime.mode}.log")
    finally:
        store.stop()
    return {"tier": "C", "mode": runtime.mode, "exit": code, "summary": summary}


def tier_s(archive: Path, runtime: Runtime, work: Path, report: Path) -> dict:
    store = Neo4j(archive, work / "neo4j-schema", SCHEMA_PORT, None, report, "schema")
    store.start()
    try:
        env = gate_env(runtime, store)
        first, first_summary = unittest_run(runtime, GATE / "schematests", "test_schema.py", env | {"GATE_SCHEMA_PASS": "first"},
                                            report / f"tier-s-first-{runtime.mode}.log")
        before, rows = store.fingerprint(work)
        second, second_summary = unittest_run(runtime, GATE / "schematests", "test_schema.py", env | {"GATE_SCHEMA_PASS": "second"},
                                              report / f"tier-s-second-{runtime.mode}.log")
        after, _ = store.fingerprint(work)
    finally:
        store.stop()
    return {"tier": "S", "mode": runtime.mode, "exit": first or second, "summary": f"first: {first_summary}; second: {second_summary}",
            "graphRows": rows, "graph": "unchanged" if before == after else "CHANGED", "before": before, "after": after}


def run(args: argparse.Namespace) -> int:
    artifact = (args.library or args.wheel).resolve()
    if not artifact.is_file():
        sys.exit(f"no file at {artifact}")
    work = args.work.resolve()
    report = args.report.resolve()
    work.mkdir(parents=True, exist_ok=True)
    report.mkdir(parents=True, exist_ok=True)
    archive = neo4j_archive(work)
    runtime = Runtime.library(artifact) if args.library else Runtime.wheel(artifact, work)
    tiers = {t.strip().upper() for t in args.tiers.split(",")}
    results = []
    if "B" in tiers:
        results.append(tier_b(archive, runtime, work, report, args.tls.resolve() if args.tls else None))
    if "C" in tiers:
        results.append(tier_c(archive, runtime, work, report))
    if "S" in tiers:
        results.append(tier_s(archive, runtime, work, report))
    record = {
        "platform": f"{platform.system()} {platform.machine()}",
        "python": platform.python_version(),
        "neo4j": NEO4J_VERSION,
        "mode": runtime.mode,
        runtime.mode: {"file": artifact.name, "bytes": artifact.stat().st_size,
                       "sha256": hashlib.sha256(artifact.read_bytes()).hexdigest()},
        "tiers": results,
    }
    (report / f"store-gate-{runtime.mode}.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    lines = ["| Tier | Mode | Result | Graph | TLS |", "|---|---|---|---|---|"]
    for r in results:
        lines.append(f"| {r['tier']} | {r['mode']} | {'pass' if r['exit'] == 0 else 'FAIL'}: {r['summary']} | "
                     f"{r.get('graph', 'n/a')} | {'yes' if r.get('tls') else 'n/a'} |")
    (report / f"store-gate-{runtime.mode}.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines))
    failed = any(r["exit"] != 0 or r.get("graph") == "CHANGED" for r in results)
    return 1 if failed else 0


def main() -> None:
    parser = argparse.ArgumentParser(description="The gate's store tiers on any platform (NATIVE-PLAT-STORE).")
    sub = parser.add_subparsers(dest="command", required=True)
    tls = sub.add_parser("tls", help="make a throw-away certificate authority and a server certificate for localhost")
    tls.add_argument("folder", type=Path)
    go = sub.add_parser("run", help="run tiers B and C against local Neo4j stores")
    what = go.add_mutually_exclusive_group(required=True)
    what.add_argument("--library", type=Path)
    what.add_argument("--wheel", type=Path)
    go.add_argument("--work", type=Path, required=True)
    go.add_argument("--report", type=Path, required=True)
    go.add_argument("--tls", type=Path)
    go.add_argument("--tiers", default="B,C,S")
    args = parser.parse_args()
    if args.command == "tls":
        make_tls(args.folder)
    else:
        sys.exit(run(args))


if __name__ == "__main__":
    main()
