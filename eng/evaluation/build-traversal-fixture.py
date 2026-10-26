#!/usr/bin/env python3
"""Build the traversal-context trial fixture (coding packs CMP-4) from this repository's own history.

Ground truth that does not come from the system under test:
  * test impact: for recent commits that changed 1-3 source files together with test files, the task is "change those
    source files" (anchors = the source paths, question = the commit subject) and the expected tests are the test files the
    same commit changed. A hit is a test the walk offers from one of those files.
  * decisions: hand-written tasks (decision-tasks.json), each naming the file an engineer would edit and the decision that
    governs it, by title, written from the decision's text.

Only files that still exist at HEAD count, since the code map is read at HEAD. Deterministic for a given HEAD.
Usage: python eng/evaluation/build-traversal-fixture.py [--max 40] [--skip 0] [--no-decisions] [--untested map.jsonl] [--out ...]
--skip passes over the newest qualifying commits: the held-out set is --skip 40 --max 40 --no-decisions.
--untested keeps only changes whose files define no type a test links to in that map (`codemap scan --output`): the
changes direct test links cannot reach (CMP-2c).
"""
import argparse, json, pathlib, subprocess, sys

ROOT = pathlib.Path(__file__).resolve().parents[2]


def git(*args):
    return subprocess.run(["git", "-C", str(ROOT), *args], capture_output=True, text=True, encoding="utf-8", check=True).stdout


def is_test(path):
    return path.startswith("tests/") or "/tests/" in path or ".Tests" in path


def is_source(path):
    return path.startswith("src/") and path.endswith(".cs") and not is_test(path) and "/obj/" not in path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--max", type=int, default=40)
    parser.add_argument("--skip", type=int, default=0)
    parser.add_argument("--no-decisions", action="store_true")
    parser.add_argument("--untested", default=None)
    parser.add_argument("--out", default="eng/evaluation/traversal-fixture.json")
    options = parser.parse_args()

    head = git("rev-parse", "HEAD").strip()
    untested = None
    if options.untested:
        records = [json.loads(line) for line in open(options.untested, encoding="utf-8")]
        tested = {l["target"] for r in records if r["type"] == "CodemapTest" for l in r["links"] if l["relation"] == "CODEMAP_TESTS"}
        defines = {r["key"]: {l["target"] for l in r["links"] if l["relation"] == "CODEMAP_DEFINES"} for r in records if r["type"] == "CodemapFile"}
        untested = {path for path, symbols in defines.items() if symbols and not symbols & tested}
    tracked = set(git("ls-files").splitlines())
    log = git("log", "--no-merges", "--name-only", "--format=@@%H%x09%s", "-n", "2000")
    tasks, commits = [], []
    for block in log.split("@@")[1:]:
        lines = [l for l in block.splitlines() if l.strip()]
        sha, _, subject = lines[0].partition("\t")
        files = lines[1:]
        if len(files) > 25:
            continue
        sources = sorted(f for f in files if is_source(f) and f in tracked)
        tests = sorted(f for f in files if is_test(f) and f.endswith(".cs") and f in tracked)
        if not (1 <= len(sources) <= 3) or not tests:
            continue
        if untested is not None and not all(s in untested for s in sources):
            continue
        commits.append(sha)
        if len(commits) <= options.skip:
            continue
        tasks.append({
            "id": f"ti-{sha[:10]}", "kind": "test-impact", "question": subject.strip(),
            "anchors": [{"kind": "path", "value": s} for s in sources],
            "expect": {"testFiles": tests},
            "source": f"git:{sha}",
        })
        if len(tasks) >= options.max:
            break

    decisions = {"tasks": []} if options.no_decisions else json.loads((ROOT / "eng" / "evaluation" / "decision-tasks.json").read_text(encoding="utf-8"))
    fixture = {
        "format": "agentmemory.traversal-fixture/v1",
        "head": head,
        "built": "build-traversal-fixture.py",
        "testImpact": {"rule": "commits with 1-3 changed source files and >=1 changed test file, <=25 files, files present at HEAD"
                               + ("; every source file defines types and none has a direct test" if untested is not None else ""),
                       "skip": options.skip, "count": len(tasks)},
        "tasks": tasks + decisions["tasks"],
    }
    out = ROOT / options.out
    out.write_text(json.dumps(fixture, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(f"{len(tasks)} test-impact tasks + {len(decisions['tasks'])} decision tasks at {head[:12]} -> {options.out}")


if __name__ == "__main__":
    sys.exit(main())
