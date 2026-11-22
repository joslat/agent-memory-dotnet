"""The gate's stub servers, for every binding's runner (NATIVE-GATE-SHARED). Standard library only.

One HTTP server on 127.0.0.1 serves:

- ``POST /axes/v1/embeddings``: the read gate's embedder. "wrong dimensions" gets a vector of 3; text with "work" the
  vector near axis 0 of the seed (``native/gate/seed/seed.cs``); anything else axis 2.
- ``POST /letters/v1/embeddings``: the write gate's embedder. Letter counts folded onto 8 axes and normalised; text
  about a zebra gets a vector of 3 (the store cannot embed it).
- ``POST /v1/chat/completions``: the writer and the update judge. The writer keeps the last message as one fact (as a
  preference when it says "prefer"); the judge answers "no". Mode ``ok``, ``fail`` (every writer call answers 500) or
  ``hold`` (writer calls wait for ``release``, then answer as ``ok``).
- ``GET /_stub/seen``: what the stubs saw, as JSON; ``POST /_stub/control`` with ``{"mode": ..., "release": bool,
  "reset": bool}``.

Run ``python stubs.py`` and read the first line of its output, ``{"port": N}``; it serves until its input closes or it
is killed. A runner in another language starts it the same way, so no language reimplements the stubs.
"""
from __future__ import annotations

import http.server
import json
import math
import socketserver
import sys
import threading

DIMENSIONS = 8
NEAR_AXIS_0 = [1.0, 0.1, 0, 0, 0, 0, 0, 0]
AXIS_2 = [0, 0, 1.0, 0, 0, 0, 0, 0]


def letters(text: str) -> list[float]:
    """Deterministic vectors from letter counts, the same rule as the native write probe."""
    vector = [0.0] * DIMENSIONS
    for c in text.lower():
        if "a" <= c <= "z":
            vector[(ord(c) - ord("a")) % DIMENSIONS] += 1.0
    norm = math.sqrt(sum(v * v for v in vector))
    return [v / norm for v in vector] if norm else [1.0] + [0.0] * (DIMENSIONS - 1)


class State:
    lock = threading.Lock()
    release = threading.Event()
    mode = "ok"

    @classmethod
    def reset(cls) -> None:
        with cls.lock:
            cls.mode = "ok"
            cls.release.clear()
            cls.seen = {"embeddings": [], "writerCalls": 0, "judgeCalls": 0, "said": [], "reasoning": [], "prompts": []}


State.reset()


class Stubs(http.server.BaseHTTPRequestHandler):
    def do_GET(self):  # noqa: N802 - the http.server naming
        if self.path == "/_stub/seen":
            with State.lock:
                self.answer(json.loads(json.dumps(State.seen)))
            return
        self.send_error(404)

    def do_POST(self):  # noqa: N802
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))) or b"{}")
        if self.path == "/_stub/control":
            if body.get("reset"):
                State.reset()
            if "mode" in body:
                State.mode = body["mode"]
            if body.get("release"):
                State.release.set()
            self.answer({"mode": State.mode})
        elif self.path == "/axes/v1/embeddings":
            self.embeddings(body, self.axes)
        elif self.path in ("/letters/v1/embeddings", "/v1/embeddings"):
            self.embeddings(body, lambda t: [1.0, 0.0, 0.0] if "zebra" in t.lower() else letters(t))
        elif self.path == "/v1/chat/completions":
            self.chat(body)
        else:
            self.send_error(404)

    @staticmethod
    def axes(text: str) -> list[float]:
        if text == "wrong dimensions":
            return [1.0, 0.0, 0.0]
        return NEAR_AXIS_0 if "work" in text else AXIS_2

    def embeddings(self, body: dict, rule) -> None:
        with State.lock:
            State.seen["embeddings"].append({"path": self.path, "model": body.get("model"), "input": body.get("input"),
                                             "authorization": self.headers.get("Authorization")})
        self.answer({"object": "list", "model": body.get("model"), "usage": {"prompt_tokens": 1},
                     "data": [{"object": "embedding", "index": i, "embedding": rule(t)} for i, t in enumerate(body["input"])]})

    def chat(self, body: dict) -> None:
        system = body["messages"][0]["content"]
        if system.startswith("You keep a person's long-term memory"):
            with State.lock:
                State.seen["judgeCalls"] += 1
            self.reply('{"p1": "no"}')
            return
        with State.lock:
            State.seen["writerCalls"] += 1
            State.seen["reasoning"].append(body.get("reasoning_effort"))
            State.seen["prompts"].append(body["messages"][-1]["content"])
        if State.mode == "fail":
            self.send_response(500)
            self.end_headers()
            return
        if State.mode == "hold":
            State.release.wait(120)
        context = body["messages"][-1]["content"]
        said = next(line.split("(LAST MESSAGE): ", 1)[1] for line in context.splitlines() if "(LAST MESSAGE): " in line)
        with State.lock:
            State.seen["said"].append(said)
        words = said.rstrip(".")
        operation = ({"op": "add", "kind": "preference", "text": words, "quote": said} if "prefer" in said
                     else {"op": "add", "kind": "fact", "text": f"user | said | {words}", "quote": said})
        operation.update({"id": None, "until": None, "ref": None})
        self.reply(json.dumps({"ops": [operation]}))

    def reply(self, content: str) -> None:
        self.answer({"model": "stub-writer", "choices": [{"message": {"role": "assistant", "content": content},
                                                          "finish_reason": "stop"}],
                     "usage": {"prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15}})

    def answer(self, document: dict) -> None:
        data = json.dumps(document).encode()
        try:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        except (BrokenPipeError, ConnectionResetError):
            pass  # a held call whose caller went away

    def log_message(self, *_):
        pass


class LocalHTTPServer(http.server.ThreadingHTTPServer):
    """Bound without HTTPServer's reverse lookup of its own name (socket.getfqdn): about 35 s on macOS runners."""

    daemon_threads = True

    def server_bind(self):
        socketserver.TCPServer.server_bind(self)
        self.server_name, self.server_port = self.server_address[:2]


def start(port: int = 0) -> LocalHTTPServer:
    """In this process: for the Python runner, and for a test that wants the stubs without a child process."""
    server = LocalHTTPServer(("127.0.0.1", port), Stubs)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def main() -> None:
    server = start()
    print(json.dumps({"port": server.server_address[1]}), flush=True)
    try:
        sys.stdin.read()  # serves until the runner closes our input
    finally:
        server.shutdown()


if __name__ == "__main__":
    main()
