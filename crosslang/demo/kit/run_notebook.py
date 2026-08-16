"""Executes the demo notebook and reports whether every cell ran.

The notebook is the artifact people will actually open in the room, so "it should work" is not a
status. This runs it start to finish against the live host and fails loudly on the first error --
including the `assert`s in the `as_of` cell, which are the notebook's own void witnesses.

    python crosslang/demo/kit/run_notebook.py [--write]

`--write` keeps the executed outputs in place (useful for the fallback screencast); the default
leaves the committed notebook clean, because outputs in a committed notebook go stale silently.
"""

from __future__ import annotations

import argparse
import pathlib
import sys

import nbformat
from nbclient import NotebookClient
from nbclient.exceptions import CellExecutionError

HERE = pathlib.Path(__file__).parent
NOTEBOOK = HERE / "agentmemory_langgraph.ipynb"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true", help="keep executed outputs in the file")
    args = parser.parse_args()

    notebook = nbformat.read(NOTEBOOK, as_version=4)
    client = NotebookClient(notebook, timeout=180, kernel_name="python3", resources={
        # cwd matters: cell 1 resolves the adapter as the parent directory.
        "metadata": {"path": str(HERE)},
    })

    try:
        client.execute()
    except CellExecutionError as error:
        print(f"FAILED — a cell raised:\n{error}", file=sys.stderr)
        return 1

    executed = sum(1 for c in notebook.cells if c.cell_type == "code")
    print(f"OK — {executed} code cells executed, no exceptions (asserts included).")

    for cell in notebook.cells:
        if cell.cell_type != "code":
            continue
        for output in cell.get("outputs", []):
            if output.get("output_type") == "stream":
                print("".join(output["text"]), end="")

    if args.write:
        nbformat.write(notebook, NOTEBOOK)
        print(f"\nwrote executed outputs to {NOTEBOOK.name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
