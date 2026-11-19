# SPDX-License-Identifier: Apache-2.0
"""End to end: packs made now by the pack builder, read by every ampack client package as its users install it.

What it does, in order:
  1. builds the pack builder (tools/Ampack) and the client packages from this checkout: the Python wheel, the npm
     package, the Rust crates and command, the NuGet library and its command;
  2. installs each package the way its users do, each in a new place of its own: a virtual environment, a new npm
     project, a new .NET 8 console project and a tool folder, a cargo install root;
  3. makes new keys and new packs with the builder, and the documents around them (trust policies, revocation lists);
  4. gives every case to every client and compares. Each client must reach the expected verdict. Where a pack verifies,
     every library must have read the same pack: its identity, its digest, the hash of every file, every record and
     every member; and what was read must be what was written in step 3.

usage: python ampack/e2e/run.py --work <folder outside the checkout> [--jobs N] [--skip-build] [--hub <folder>]

  --work        where everything is built, installed and made; created when missing, emptied part by part on a new run
  --jobs N      at most N build jobs at a time (dotnet -m:N, cargo -j N)
  --skip-build  use the packages and the installs a former run left in --work; only steps 3 and 4 run
  --hub <folder> also the published packs of a pack hub, from a checkout of it (trust.json, and packs/ with the .ampack
                files): every client must verify each of them, and every library must read the same pack

Needs Python 3.12, Node 20 or later with npm, a Rust toolchain with the target wasm32-unknown-unknown, the .NET SDK and
the .NET 8 runtime. Exit code 0 when every check held, 1 when one did not, 2 for a problem of the environment.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
READER = REPO / "reader-rs"
EXE = ".exe" if os.name == "nt" else ""
MARK = ".ampack-e2e"
WASM_TOOLS = "1.263.0"
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")


class Problem(Exception):
    """The environment or a build step failed: not a verdict about the packages."""


def tool(name):
    found = shutil.which(name)
    if not found:
        raise Problem(f"'{name}' is not on the PATH")
    return found


def run(command, *, cwd=None, env=None, accept=(0,)):
    """Runs a command; returns (exit code, output). An exit code outside `accept` is a Problem (accept=None: any)."""
    command = [str(part) for part in command]
    done = subprocess.run(command, cwd=cwd, env=dict(os.environ, **(env or {})), capture_output=True, check=False)
    output = done.stdout.decode("utf-8", "replace") + done.stderr.decode("utf-8", "replace")
    if accept is not None and done.returncode not in accept:
        raise Problem(f"{' '.join(command)}\n  ended with exit code {done.returncode}:\n{output[-3000:]}")
    return done.returncode, output


def fresh(folder):
    """An empty folder at that place. Only ever inside the work folder, which carries the mark."""
    if folder.exists():
        shutil.rmtree(folder)
    folder.mkdir(parents=True)
    return folder


def only(folder, pattern):
    found = sorted(folder.glob(pattern))
    if len(found) != 1:
        raise Problem(f"expected one {pattern} in {folder}, found {len(found)}")
    return found[0]


def python_of(environment):
    return environment / ("Scripts" if os.name == "nt" else "bin") / f"python{EXE}"


# ----------------------------------------------------------------------------------------------------------------------
# 1. Build the builder and the packages
# ----------------------------------------------------------------------------------------------------------------------

def build(work, jobs):
    made, packages = work / "build", fresh(work / "packages")
    made.mkdir(exist_ok=True)
    dotnet, cargo, npm = tool("dotnet"), tool("cargo"), tool("npm")
    cargo_env = {"CARGO_TARGET_DIR": str(made / "cargo")}
    j = ["-j", str(jobs)] if jobs else []
    msbuild = ["-c", "Release", "--artifacts-path", made / "dotnet", "-nodeReuse:false", *([f"-m:{jobs}"] if jobs else [])]

    print("building: the pack builder and the NuGet packages", flush=True)
    run([dotnet, "build", REPO / "tools" / "Ampack" / "Ampack.csproj", *msbuild])
    run([dotnet, "pack", REPO / "src" / "Ampack" / "Ampack.csproj", *msbuild, "-o", packages / "nuget"])
    run([dotnet, "pack", REPO / "tools" / "Ampack.Tool" / "Ampack.Tool.csproj", *msbuild, "-o", packages / "nuget"])

    print("building: the Python package", flush=True)
    run([sys.executable, REPO / "ampack" / "python" / "sync_sources.py"])
    builder = made / "python-build"
    if not python_of(builder).exists():
        run([sys.executable, "-m", "venv", builder])
        run([python_of(builder), "-m", "pip", "install", "--disable-pip-version-check", "-q", "build==1.6.1"])
    run([python_of(builder), "-m", "build", "--outdir", packages / "python", REPO / "ampack" / "python"])

    print("building: the WebAssembly component and the npm package", flush=True)
    run([cargo, "build", "-p", "ampack-wasm", "--target", "wasm32-unknown-unknown", "--release", "--locked", *j],
        cwd=READER, env=cargo_env)
    (READER / "dist").mkdir(exist_ok=True)
    run([wasm_tools(made, cargo, j), "component", "new",
         made / "cargo" / "wasm32-unknown-unknown" / "release" / "ampack_wasm.wasm", "-o", READER / "dist" / "ampack-reader.wasm"])
    run([npm, "install", "--no-audit", "--no-fund"], cwd=READER / "js")
    run([npm, "run", "build"], cwd=READER / "js")
    (packages / "npm").mkdir()
    run([npm, "pack", "--pack-destination", packages / "npm"], cwd=READER / "js")

    print("building: the Rust crates", flush=True)
    run([sys.executable, READER / "sync_sources.py"])
    # Packages both crates and builds each from its own package, as crates.io serves it.
    run([cargo, "package", "-p", "ampack-core", "-p", "ampack", "--allow-dirty", "--locked", *j], cwd=READER, env=cargo_env)
    (packages / "crates").mkdir()
    for crate in sorted((made / "cargo" / "package").glob("*.crate")):
        shutil.copy(crate, packages / "crates")


def wasm_tools(made, cargo, j):
    for root in (READER / ".tools", made / "tools"):
        candidate = root / "bin" / f"wasm-tools{EXE}"
        if candidate.exists() and WASM_TOOLS in run([candidate, "--version"])[1]:
            return candidate
    print(f"building: wasm-tools {WASM_TOOLS} (once)", flush=True)
    run([cargo, "install", "wasm-tools", "--version", f"={WASM_TOOLS}", "--locked", "--root", made / "tools",
         "--no-default-features", "--features", "component", *j])
    return made / "tools" / "bin" / f"wasm-tools{EXE}"


# ----------------------------------------------------------------------------------------------------------------------
# 2. Install each package as its users do
# ----------------------------------------------------------------------------------------------------------------------

DRIVER_PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RollForward>Major</RollForward>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>driver</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Ampack" Version="{version}" />
  </ItemGroup>
</Project>
"""

# Ampack and Ampack.Tool come from the packages built here and from nowhere else, whatever nuget.org holds by then.
NUGET_CONFIG = """<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="built-here" value="{feed}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="built-here">
      <package pattern="Ampack" />
      <package pattern="Ampack.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"""


def install(work, jobs):
    packages, users = work / "packages", fresh(work / "users")
    dotnet, cargo, npm = tool("dotnet"), tool("cargo"), tool("npm")

    print("installing: the wheel in a new virtual environment", flush=True)
    run([sys.executable, "-m", "venv", users / "python"])
    run([python_of(users / "python"), "-m", "pip", "install", "--disable-pip-version-check", "-q",
         only(packages / "python", "*.whl")])
    shutil.copy(HERE / "drivers" / "driver.py", users / "python" / "driver.py")

    print("installing: the npm package in a new project", flush=True)
    project = fresh(users / "javascript")
    (project / "package.json").write_text('{"name": "ampack-e2e-user", "private": true, "type": "module"}\n', encoding="utf-8")
    run([npm, "install", "--no-audit", "--no-fund", only(packages / "npm", "*.tgz")], cwd=project)
    shutil.copy(HERE / "drivers" / "driver.mjs", project / "driver.mjs")

    print("installing: the NuGet library in a new .NET 8 project, and the command as a tool", flush=True)
    library = only(packages / "nuget", "Ampack.[0-9]*.nupkg")
    version = library.name[len("Ampack."):-len(".nupkg")]
    project = fresh(users / "dotnet")
    (project / "driver.csproj").write_text(DRIVER_PROJECT.format(version=version), encoding="utf-8")
    (project / "nuget.config").write_text(NUGET_CONFIG.format(feed=packages / "nuget"), encoding="utf-8")
    shutil.copy(HERE / "drivers" / "driver.cs", project / "Program.cs")
    # A package cache of its own: the machine's cache may hold an earlier build under the same version.
    cache = {"NUGET_PACKAGES": str(users / "nuget-cache")}
    run([dotnet, "build", "-c", "Release", "-o", project / "out", "-nodeReuse:false", *([f"-m:{jobs}"] if jobs else [])],
        cwd=project, env=cache)
    run([dotnet, "tool", "install", "Ampack.Tool", "--tool-path", users / "dotnet-tool", "--version", version,
         "--configfile", project / "nuget.config"], cwd=project, env=cache)

    print("installing: the Rust command with cargo install", flush=True)
    run([cargo, "install", "--path", READER / "crates" / "ampack-cli", "--root", users / "rust", "--locked", "--force",
         *(["-j", str(jobs)] if jobs else [])], cwd=READER, env={"CARGO_TARGET_DIR": str(work / "build" / "cargo")})


def clients(work):
    """Each client as (kind, command). A library runs its driver and prints JSON; a command answers with its exit code."""
    users = work / "users"
    python = python_of(users / "python")
    return {
        "python": ("library", [python, "-I", users / "python" / "driver.py"]),
        "python cmd": ("command", [python, "-I", "-m", "ampack", "verify"]),
        "javascript": ("library", [tool("node"), users / "javascript" / "driver.mjs"]),
        "dotnet": ("library", [tool("dotnet"), users / "dotnet" / "out" / "driver.dll"]),
        "dotnet cmd": ("command", [users / "dotnet-tool" / f"ampack{EXE}", "verify"]),
        "rust cmd": ("command", [users / "rust" / "bin" / f"ampack-rs{EXE}", "verify"]),
    }


# ----------------------------------------------------------------------------------------------------------------------
# 3. Make keys, packs and the documents around them
# ----------------------------------------------------------------------------------------------------------------------

NOTES = [
    ("opening", "Opening", "A pack is one signed file."),
    ("unicode", "Zürich, 東京, São Paulo", "Text outside ASCII comes back as written: naïve café, Ελληνικά, 日本語, emoji 🧠."),
    ("quotes", "Quotes and \\ slashes", 'A "quoted" word, a tab\tand a line\nbreak inside one value.'),
    ("long", "A long value", "word " * 300),
    ("closing", "Closing", "The last record links back to the first."),
]


def produce(work):
    made = fresh(work / "made")
    builder = [tool("dotnet"), work / "build" / "dotnet" / "bin" / "Ampack" / "release" / "Ampack.dll"]

    def ampack(*arguments):
        return run([*builder, *arguments], cwd=made)[1]

    ampack("keygen", "--out", "keys", "--name", "publisher", "--prefix", "io.example", "--role", "publisher", "--policy", "trust.json")
    ampack("keygen", "--out", "keys", "--name", "revoker", "--prefix", "io.example", "--role", "revoker", "--policy", "trust.json")
    ampack("keygen", "--out", "keys", "--name", "elsewhere", "--prefix", "org.elsewhere", "--role", "publisher", "--policy", "trust.json")
    ampack("keygen", "--out", "other-keys", "--name", "stranger", "--prefix", "io.example", "--role", "publisher", "--policy", "other-host.json")

    written = {}

    def pack(name, key="publisher"):
        """A knowledge pack with the NOTES as records, each linked to the one before; returns its digest."""
        folder = made / name
        ampack("init", name, "--id", f"io.example.e2e.{name}", "--kind", "knowledge", "--owner", "End to end")
        ontology = json.loads((folder / "ontology.json").read_text(encoding="utf-8"))
        kind, relation = ontology["types"][0]["name"], f"{ontology['labelPrefix'].upper()}_FOLLOWS"
        ontology["relations"] = [{"name": relation, "from": kind, "to": kind}]
        (folder / "ontology.json").write_text(json.dumps(ontology, indent=2) + "\n", encoding="utf-8", newline="\n")
        records, before = [], None
        for key_of, title, text in NOTES:
            record = {"type": kind, "key": f"{name}/{key_of}", "properties": {"title": title, "text": text}}
            if before:
                record["links"] = [{"relation": relation, "target": before}]
            records.append(record)
            before = record["key"]
        lines = [json.dumps(record, ensure_ascii=False, separators=(",", ":")) for record in records]
        only(folder / "records", "*.jsonl").write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
        output = ampack("build", name, "--out", f"{name}.ampack", "--key", f"keys/{key}.key.pem")
        written[f"io.example.e2e.{name}"] = records
        return DIGEST.search(output).group(0)

    digests = {"notes": pack("notes"), "terms": pack("terms"), "foreign": pack("foreign", key="elsewhere")}
    ampack("set", "both.ampack", "notes.ampack", "terms.ampack", "--id", "io.example.e2e.both", "--version", "1.0.0",
           "--key", "keys/publisher.key.pem", "--policy", "trust.json")

    now = datetime.now(timezone.utc)
    tomorrow = (now + timedelta(days=1)).strftime("%Y-%m-%dT%H:%M:%SZ")
    ampack("revoke", "revokes-notes.json", "--key", "keys/revoker.key.pem", "--expires", tomorrow, "--digests", digests["notes"])
    ampack("revoke", "revokes-another.json", "--key", "keys/revoker.key.pem", "--expires", tomorrow, "--digests", "sha256:" + "ab" * 32)

    whole = (made / "notes.ampack").read_bytes()
    changed = bytearray(whole)
    changed[len(whole) // 2] ^= 0x01
    (made / "notes-changed.ampack").write_bytes(bytes(changed))
    (made / "notes-cut.ampack").write_bytes(whole[: len(whole) * 6 // 10])
    (made / "not-a-pack.ampack").write_bytes(b"This is not a pack.\n" * 20)

    def case(name, pack_file, trust="trust.json", revocations=(), at=None, verified=False, commands=True):
        return {"name": name, "pack": made / pack_file, "trust": made / trust, "revocations": [made / r for r in revocations],
                "at": at, "verified": verified, "commands": commands}

    cases = [
        case("a new pack", "notes.ampack", verified=True),
        case("a set of two packs", "both.ampack", verified=True),
        case("a revocation list naming another pack", "notes.ampack", revocations=["revokes-another.json"], verified=True),
        case("one byte changed", "notes-changed.ampack"),
        case("the file cut short", "notes-cut.ampack"),
        case("not a pack at all", "not-a-pack.ampack"),
        case("a host that does not trust the publisher", "notes.ampack", trust="other-host.json"),
        case("a key signing outside its prefix", "foreign.ampack"),
        case("the pack's digest revoked", "notes.ampack", revocations=["revokes-notes.json"]),
        case("the set, one member revoked", "both.ampack", revocations=["revokes-notes.json"]),
        # The commands take no clock, so only the libraries are asked.
        case("a revocation list past its expiry", "notes.ampack", revocations=["revokes-another.json"],
             at=(now + timedelta(days=2)).strftime("%Y-%m-%dT%H:%M:%SZ"), commands=False),
    ]
    return cases, digests, written


# ----------------------------------------------------------------------------------------------------------------------
# 4. Ask every client and compare
# ----------------------------------------------------------------------------------------------------------------------

def ask(kind, command, case):
    """One client's answer to one case: {"verified": bool, ...}, or {"defect": text}."""
    arguments = [case["pack"], case["trust"]]
    if kind == "library":
        for document in case["revocations"]:
            arguments += ["--revocations", document]
        if case["at"]:
            arguments += ["--at", case["at"]]
        code, output = run([*command, *arguments], accept=None)
        if code != 0:
            return {"defect": f"exit code {code}: {output[-600:]}"}
        try:
            return json.loads(output.strip().splitlines()[-1])
        except (ValueError, IndexError):
            return {"defect": f"no JSON answer: {output[-600:]}"}
    code, output = run([*command, *arguments, *case["revocations"]], accept=None)
    if code not in (0, 1):
        return {"defect": f"exit code {code}: {output[-600:]}"}
    return {"verified": code == 0, "digests": DIGEST.findall(output), "said": output.strip()[-300:]}


def same(a, b, path="pack"):
    """The first difference between two packs as read, or None. A member's digest a client does not give is not compared."""
    if isinstance(a, dict) and isinstance(b, dict):
        if a.keys() != b.keys():
            return f"{path}: fields {sorted(a)} against {sorted(b)}"
        for name in a:
            if name == "digest" and (a[name] is None or b[name] is None):
                continue
            if (difference := same(a[name], b[name], f"{path}.{name}")):
                return difference
        return None
    if isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            return f"{path}: {len(a)} against {len(b)} entries"
        for index, (left, right) in enumerate(zip(a, b)):
            if (difference := same(left, right, f"{path}[{index}]")):
                return difference
        return None
    return None if a == b and type(a) is type(b) else f"{path}: {a!r} against {b!r}"


def as_written(pack, written):
    """What a library read against what step 3 wrote, pack by pack."""
    for each in [pack, *pack["members"]]:
        expected = written.get(each["id"])
        if expected is None:
            continue
        read = [{"type": r["type"], "key": r["key"], "properties": r["properties"],
                 **({"links": [{"relation": a, "target": b} for a, b in r["links"]]} if r["links"] else {})}
                for r in each["records"]]
        if read != expected:
            return f"{each['id']}: the records read are not the records written"
    return None


def judge(cases, digests, written, all_clients):
    failures, rows = [], []
    for case in cases:
        row, read = {}, {}
        for name, (kind, command) in all_clients.items():
            if kind == "command" and not case["commands"]:
                row[name] = "-"
                continue
            answer = ask(kind, command, case)
            problem = None
            if "defect" in answer:
                problem = answer["defect"]
            elif answer["verified"] != case["verified"]:
                problem = f"said {'verified' if answer['verified'] else 'refused'}: {answer.get('reason') or answer.get('said') or ''}"
            elif answer["verified"] and kind == "library":
                read[name] = answer["pack"]
                problem = as_written(answer["pack"], written)
            if not problem and answer["verified"] and case["pack"].name == "notes.ampack":
                given = [answer["pack"]["digest"]] if kind == "library" else answer["digests"]
                if kind == "library" and given != [digests["notes"]] or kind == "command" and given and digests["notes"] not in given:
                    problem = f"digest {given} is not the builder's {digests['notes']}"
            row[name] = "FAIL" if problem else "ok"
            if problem:
                failures.append(f"{case['name']} / {name}: {problem}")
        # The libraries that read the same pack stand together; a strict majority is taken as the reading the others miss.
        groups = []
        for name in sorted(read):
            for group in groups:
                if same(read[group[0]], read[name]) is None:
                    group.append(name)
                    break
            else:
                groups.append([name])
        if len(groups) > 1:
            groups.sort(key=len, reverse=True)
            agreed = groups[0] if len(groups[0]) > len(groups[1]) else []
            for group in groups:
                if group is agreed:
                    continue
                against = agreed or next(other for other in groups if other is not group)
                for name in group:
                    row[name] = "FAIL"
                failures.append(f"{case['name']}: {', '.join(group)} read another pack than {', '.join(against)}: "
                                f"{same(read[against[0]], read[group[0]])}")
        rows.append((case, row))
    return rows, failures


def report(rows, failures, all_clients, work):
    width = max(len(case["name"]) for case, _ in rows) + 2
    print()
    print("case".ljust(width) + "expected".ljust(10) + "".join(name.ljust(12) for name in all_clients))
    for case, row in rows:
        print(case["name"].ljust(width) + ("verified" if case["verified"] else "refused").ljust(10)
              + "".join(row[name].ljust(12) for name in all_clients))
    asked = sum(1 for _, row in rows for value in row.values() if value != "-")
    print(f"\n{asked} answers from {len(all_clients)} clients on {len(rows)} cases; {len(failures)} failed.")
    for failure in failures:
        print(f"  FAILED {failure}")
    (work / "results.json").write_text(json.dumps({
        "when": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "cases": [{"name": case["name"], "expected": "verified" if case["verified"] else "refused", "answers": row}
                  for case, row in rows],
        "failures": failures}, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description="End-to-end test of the ampack client packages.")
    parser.add_argument("--work", required=True)
    parser.add_argument("--jobs", type=int)
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--hub")
    options = parser.parse_args()
    if sys.version_info[:2] != (3, 12):
        print("error: the Python client needs Python 3.12 exactly; run this with it")
        return 2
    work = Path(options.work).resolve()
    if work == REPO or REPO in work.parents:
        print("error: --work must be a folder outside the checkout")
        return 2
    if work.exists() and any(work.iterdir()) and not (work / MARK).exists():
        print(f"error: {work} is not empty and was not made by this script")
        return 2
    work.mkdir(parents=True, exist_ok=True)
    (work / MARK).write_text("Made by ampack/e2e/run.py; its folders are emptied on each run.\n", encoding="utf-8")
    try:
        if not options.skip_build:
            build(work, options.jobs)
            install(work, options.jobs)
        cases, digests, written = produce(work)
        if options.hub:
            hub = Path(options.hub).resolve()
            published = sorted((hub / "packs").rglob("*.ampack"))
            if not published or not (hub / "trust.json").is_file():
                raise Problem(f"{hub} holds no trust.json or no packs/**/*.ampack")
            cases += [{"name": f"hub: {pack.stem}", "pack": pack, "trust": hub / "trust.json", "revocations": [], "at": None,
                       "verified": True, "commands": True} for pack in published]
        all_clients = clients(work)
        rows, failures = judge(cases, digests, written, all_clients)
    except Problem as problem:
        print(f"error: {problem}")
        return 2
    report(rows, failures, all_clients, work)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
