"""Records and replays the demo as a terminal transcript — the catastrophic fallback.

Live demos fail; recordings don't. This captures a real run (real host, real Neo4j, real output) into
a plain text transcript, and replays it with typing cadence. **The replay needs nothing**: no host, no
database, no network, no Python packages beyond the standard library. That is the entire point — it is
the artifact you reach for when the room is watching and the container is dead.

    python screencast.py --record     # runs the demo for real, captures the transcript
    python screencast.py              # replays it
    python screencast.py --fast       # replays instantly (for checking, not for the room)

It is a *transcript*, not a video. See RECORDING.md for the video step, which needs a human to press
record — and which should be made from this replay, so the two can never disagree.
"""

from __future__ import annotations

import argparse
import os
import pathlib
import random
import subprocess
import sys
import time

HERE = pathlib.Path(__file__).parent
TRANSCRIPT = HERE / "screencast.txt"
PROMPT = "$ "

# The commands the recording runs, in run-sheet order. Each is (label shown as if typed, argv).
STEPS: list[tuple[str, list[str]]] = [
    ("python crosslang/demo/kit/preflight.py",
     [sys.executable, str(HERE / "preflight.py")]),
    ("python crosslang/demo/demo_langgraph.py",
     [sys.executable, str(HERE.parent / "demo_langgraph.py")]),
    ("python crosslang/demo/kit/run_notebook.py",
     [sys.executable, str(HERE / "run_notebook.py")]),
]

# nbclient and the debugger write these to stderr on Windows; they are environment noise, not output,
# and leaving them in a fallback recording would make a working run look broken to the room.
NOISE = ("Debugger warning", "frozen_modules", "frozen modules", "debugger miss breakpoints",
         "PYDEVD", "MissingIDField", "validate(nb)", "zmq", "_get_loop", "RuntimeWarning",
         "Note: Debugging", "site-packages")


def record() -> int:
    # The transcript is full of ✓/✗/box-drawing, and the child processes inherit the console's code
    # page unless told otherwise. Without this the recording captures mojibake -- which would then be
    # the thing shown to the room at the exact moment nothing else is working.
    environment = {**os.environ, "PYTHONIOENCODING": "utf-8"}

    chunks: list[str] = []
    for label, argv in STEPS:
        print(f"recording: {label}")
        result = subprocess.run(
            argv, capture_output=True, cwd=HERE.parent.parent.parent, env=environment)
        streams = (result.stdout or b"").decode("utf-8", "replace") \
            + (result.stderr or b"").decode("utf-8", "replace")
        output = "\n".join(
            line for line in streams.splitlines()
            if not any(noise in line for noise in NOISE)
        )
        if result.returncode != 0:
            # A failed run must never become the fallback recording. The whole value of this artifact
            # is that it shows a working system; capturing a broken one would hand the room a
            # confident-looking failure at the exact moment nothing else is working.
            print(f"\nABORTED — `{label}` exited {result.returncode}. Nothing written.\n"
                  f"{output[-2000:]}", file=sys.stderr)
            return 1
        chunks.append(f"{PROMPT}{label}\n{output.rstrip()}\n")

    TRANSCRIPT.write_text("\n".join(chunks), encoding="utf-8")
    lines = TRANSCRIPT.read_text(encoding="utf-8").count("\n")
    print(f"\nwrote {TRANSCRIPT.name} — {len(STEPS)} commands, {lines} lines")
    return 0


def play(fast: bool) -> int:
    # Same reason as the recording side: a console on a legacy code page turns the transcript into
    # question marks, and this is the artifact that has to work when nothing else does.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    if not TRANSCRIPT.exists():
        print(f"no transcript at {TRANSCRIPT}. Run --record against a live host first.", file=sys.stderr)
        return 1

    for line in TRANSCRIPT.read_text(encoding="utf-8").splitlines():
        if fast:
            print(line)
            continue

        if line.startswith(PROMPT):
            # Type the command out. The pause before output is what makes a replay read as a session
            # rather than as a file being cat'ed, which is the only thing the room would notice.
            print(PROMPT, end="", flush=True)
            for character in line[len(PROMPT):]:
                print(character, end="", flush=True)
                time.sleep(random.uniform(0.012, 0.045))
            print()
            time.sleep(0.6)
        else:
            print(line, flush=True)
            time.sleep(0.035)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--record", action="store_true", help="capture a fresh transcript (needs a live host)")
    parser.add_argument("--fast", action="store_true", help="replay with no delays")
    args = parser.parse_args()
    return record() if args.record else play(args.fast)


if __name__ == "__main__":
    sys.exit(main())
