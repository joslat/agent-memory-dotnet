"""A tap between the speech server and its conversation model, for trying the recipe by hand.

It forwards each OpenAI-compatible request to the real endpoint and streams the answer back as it arrives (no added
wait before the first word), adds the endpoint's key from an environment variable (the server never holds it), and
writes one line per call to a log: the time, the model, the status, the time to the first byte and to the end, the
shape of the request (message roles and content-part types, tool names, the other request keys) and, when the call
fails, the endpoint's error text. Message text is written only with --with-text (it is the conversation).

Usage: python model_tap.py <upstream base URL, e.g. https://api.example.com/v1> <port> <log file>
         [--key-env NAME] [--with-text]
"""
from __future__ import annotations

import argparse
import json
import os
import ssl
import sys
import threading
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

AGENT = "agentmemory-model-tap/1.0"
args = argparse.ArgumentParser()
args.add_argument("upstream")
args.add_argument("port", type=int)
args.add_argument("log")
args.add_argument("--key-env", default="")
args.add_argument("--with-text", action="store_true")
options = args.parse_args()
KEY = os.environ.get(options.key_env, "") if options.key_env else ""
if options.key_env and not KEY:
    sys.exit(f"refuse: the environment variable {options.key_env} is empty")
UPSTREAM = options.upstream.rstrip("/")
_lock = threading.Lock()


def log(entry: dict) -> None:
    with _lock, open(options.log, "a", encoding="utf-8") as out:
        out.write(json.dumps(entry, ensure_ascii=False) + "\n")


def shape(body: dict) -> dict:
    messages = []
    for message in body.get("messages", []):
        content = message.get("content")
        parts = ([part.get("type") for part in content if isinstance(part, dict)] if isinstance(content, list)
                 else ["text" if isinstance(content, str) else type(content).__name__])
        item = {"role": message.get("role"), "parts": parts}
        if message.get("tool_calls"):
            item["tool_calls"] = [call.get("function", {}).get("name") for call in message["tool_calls"]]
        if message.get("tool_call_id"):
            item["tool_call_id"] = True
        if options.with_text and isinstance(content, str):
            item["text"] = content[:300]
        messages.append(item)
    return {"model": body.get("model"), "stream": body.get("stream"), "messages": messages,
            "tools": [tool.get("function", tool).get("name") for tool in body.get("tools") or []],
            "keys": sorted(k for k in body if k not in ("messages", "tools", "model", "stream"))}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"   # the body ends when the connection closes, so chunks pass straight through

    def do_GET(self):  # noqa: N802 - model listings and health checks pass through
        self._forward(None)

    def do_POST(self):  # noqa: N802
        self._forward(self.rfile.read(int(self.headers.get("Content-Length", "0"))))

    def _forward(self, raw: bytes | None) -> None:
        started = time.monotonic()
        path = self.path[3:] if self.path.startswith("/v1") else self.path
        entry: dict = {"t": time.strftime("%H:%M:%S"), "path": path}
        try:
            body = json.loads(raw) if raw else None
            if isinstance(body, dict):
                entry["request"] = shape(body)
        except ValueError:
            entry["request"] = "not JSON"
        headers = {"Content-Type": "application/json", "User-Agent": AGENT}
        if KEY:
            headers["Authorization"] = f"Bearer {KEY}"
        elif self.headers.get("Authorization"):
            headers["Authorization"] = self.headers["Authorization"]
        request = urllib.request.Request(UPSTREAM + path, data=raw, method="POST" if raw is not None else "GET",
                                         headers=headers)
        try:
            response = urllib.request.urlopen(request, timeout=300, context=ssl.create_default_context())
        except urllib.error.HTTPError as e:
            error = e.read()
            entry.update(status=e.code, ms=round((time.monotonic() - started) * 1000),
                         error=error.decode(errors="replace")[:2000])
            log(entry)
            self.send_response(e.code)
            self.send_header("Content-Type", e.headers.get("Content-Type", "application/json"))
            self.end_headers()
            self.wfile.write(error)
            return
        except Exception as e:  # noqa: BLE001 - an unreachable endpoint is reported as 502
            entry.update(status=502, error=f"{type(e).__name__}: {e}")
            log(entry)
            self.send_response(502)
            self.end_headers()
            return
        self.send_response(response.status)
        self.send_header("Content-Type", response.headers.get("Content-Type", "application/json"))
        self.end_headers()
        first = None
        size = 0
        while True:
            chunk = response.read1(65536) if hasattr(response, "read1") else response.read(65536)
            if not chunk:
                break
            if first is None:
                first = round((time.monotonic() - started) * 1000)
            size += len(chunk)
            self.wfile.write(chunk)
            self.wfile.flush()
        entry.update(status=response.status, first_byte_ms=first, ms=round((time.monotonic() - started) * 1000),
                     bytes=size)
        log(entry)

    def log_message(self, *_):
        pass


if __name__ == "__main__":
    print(f"model tap on 0.0.0.0:{options.port} -> {UPSTREAM}", flush=True)
    ThreadingHTTPServer(("0.0.0.0", options.port), Handler).serve_forever()
