"""A counting proxy for SPEECH-BENCH's paid writer: every call the speech server's library makes to the writer model (and
the update judge) goes through it, so the run's call count and tokens are measured, not estimated.

It forwards POST .../chat/completions to the real endpoint with the key taken from the environment variable named on the
command line (the server itself gets no key), and refuses calls beyond a cap with HTTP 429 (the library then reports the
write as not saved, and the run's report shows it). After every call it rewrites the tally file: calls, refused calls,
failures, input and output tokens, and per call its time, kind (writer, judge, other), status, milliseconds and tokens.
No message text and no key is ever written or printed.
Usage: python3 writer_meter.py <upstream-url> <key-env-var> <port> <max-calls> <tally.json>
"""
from __future__ import annotations

import json
import os
import sys
import threading
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

UPSTREAM, KEY_ENV, PORT, MAX_CALLS, TALLY = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), sys.argv[5]
KEY = os.environ.get(KEY_ENV, "")
if not KEY:
    sys.exit(f"refuse: the environment variable {KEY_ENV} is empty")
# A generic agent: some endpoints refuse Python's default one with 403 (seen on 20 November 2026).
AGENT = "agentmemory-bench/1.0"
_lock = threading.Lock()
state = {"upstream": UPSTREAM.split("/v1/")[0], "max_calls": MAX_CALLS, "calls": 0, "refused": 0, "failed": 0,
         "input_tokens": 0, "output_tokens": 0, "log": []}


def save() -> None:
    tmp = TALLY + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        json.dump(state, f, indent=1)
    os.replace(tmp, TALLY)


def kind_of(request: dict) -> str:
    system = " ".join(m.get("content", "") if isinstance(m.get("content"), str) else ""
                      for m in request.get("messages", []) if m.get("role") == "system")
    if "long-term memory of one kind" in system:
        return "writer"
    if "whether the new memory replaces the stored one" in system:
        return "judge"
    return "other"


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _send(self, status: int, body: bytes, content_type: str = "application/json") -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):  # noqa: N802
        raw = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        if not self.path.rstrip("/").endswith("/chat/completions"):
            self._send(404, b'{"error":{"message":"not found"}}')
            return
        try:
            kind = kind_of(json.loads(raw or b"{}"))
        except ValueError:
            kind = "other"
        with _lock:
            if state["calls"] >= MAX_CALLS:
                state["refused"] += 1
                state["log"].append({"t": round(time.time(), 3), "kind": kind, "status": 429})
                save()
                refuse = True
            else:
                state["calls"] += 1
                refuse = False
        if refuse:
            self._send(429, b'{"error":{"message":"the benchmark call cap is reached"}}')
            return
        started = time.monotonic()
        request = urllib.request.Request(UPSTREAM, data=raw, method="POST", headers={
            "Content-Type": "application/json", "Authorization": f"Bearer {KEY}", "User-Agent": AGENT})
        status, body, usage = 502, b'{"error":{"message":"upstream unreachable"}}', {}
        try:
            with urllib.request.urlopen(request, timeout=300) as response:
                status, body = response.status, response.read()
        except urllib.error.HTTPError as e:
            status, body = e.code, e.read()
        except Exception:  # noqa: BLE001 - any transport failure is reported as a 502 to the library
            pass
        try:
            usage = json.loads(body).get("usage") or {}
        except (ValueError, AttributeError):
            usage = {}
        with _lock:
            if status != 200:
                state["failed"] += 1
            state["input_tokens"] += int(usage.get("prompt_tokens") or 0)
            state["output_tokens"] += int(usage.get("completion_tokens") or 0)
            state["log"].append({"t": round(time.time(), 3), "kind": kind, "status": status,
                                 "ms": round((time.monotonic() - started) * 1000),
                                 "in": usage.get("prompt_tokens"), "out": usage.get("completion_tokens")})
            save()
        self._send(status, body)

    def log_message(self, *_):
        pass


def check_upstream() -> None:
    """Before any paid call: the key and agent are accepted (GET .../models costs nothing). Refusal stops the run."""
    url = UPSTREAM.rsplit("/chat/completions", 1)[0] + "/models"
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers={
                "Authorization": f"Bearer {KEY}", "User-Agent": AGENT}), timeout=30) as response:
            status = response.status
    except urllib.error.HTTPError as e:
        status = e.code
    except Exception as e:  # noqa: BLE001
        status = f"unreachable ({type(e).__name__})"
    if status in (401, 403):
        sys.exit(f"refuse: the upstream refused the key or agent (HTTP {status} on {url.split('/v1/')[0]}/v1/models)")
    print(f"upstream check: HTTP {status}", flush=True)


if __name__ == "__main__":
    check_upstream()
    save()
    print(f"writer meter listening on {PORT}, at most {MAX_CALLS} calls", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
