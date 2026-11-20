"""A deterministic stand-in for an OpenAI-compatible language model, for speech-server tests without a real model.

It answers POST /v1/chat/completions (streamed as server-sent events, or not), POST /v1/embeddings (fixed vectors for
the gate's seeded store, see embed) and GET /v1/models. The reply is made from
the request, so a test can see what the model was given:
  - with no memory evidence: "You said: <last user text>."
  - when a message carries AgentMemory evidence (lines between "[memory]" and "[/memory]"): "From memory: <first line>."
Every request is appended as one JSON line to the file named by LLM_STUB_LOG, when set. Standard library only.
Usage: python3 llm_stub.py [port]
"""
from __future__ import annotations

import json
import os
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MODEL = "agentmemory-stub"
LOG = os.environ.get("LLM_STUB_LOG")
_log_lock = threading.Lock()


def embed(text: str) -> list[float]:
    """Eight-dimension vectors on the axes of the gate's seeded store (native/gate/seed/seed.cs): a question about work
    points at the works_in facts, one about how to answer at the style preferences, anything else nowhere near."""
    lowered = text.lower()
    vector = [0.0] * 8
    if "work" in lowered:
        vector[0], vector[1] = 1.0, 0.1
    elif "answer" in lowered:
        vector[2] = 1.0
    else:
        vector[5] = 1.0
    return vector


def text_of(content) -> str:
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return " ".join(part.get("text", "") for part in content if isinstance(part, dict))
    return ""


def reply_for(messages: list[dict]) -> str:
    for message in messages:
        text = text_of(message.get("content"))
        if "[memory]" in text and "[/memory]" in text:
            evidence = text.split("[memory]", 1)[1].split("[/memory]", 1)[0].strip().splitlines()
            first = next((line.strip(" -") for line in evidence if line.strip(" -")), "")
            if first:
                return f"From memory: {first}."
    users = [text_of(m.get("content")) for m in messages if m.get("role") == "user"]
    last = users[-1].strip() if users else ""
    return f"You said: {last}." if last else "Hello."


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _json(self, status: int, payload: dict) -> None:
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):  # noqa: N802
        if self.path.rstrip("/").endswith("/v1/models"):
            self._json(200, {"object": "list", "data": [{"id": MODEL, "object": "model", "owned_by": "test"}]})
        else:
            self._json(404, {"error": {"message": "not found"}})

    def do_POST(self):  # noqa: N802
        if self.path.rstrip("/").endswith("/embeddings"):
            request = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))) or b"{}")
            inputs = request.get("input", [])
            inputs = [inputs] if isinstance(inputs, str) else inputs
            self._json(200, {"object": "list", "model": request.get("model", MODEL), "usage": {"prompt_tokens": 1},
                             "data": [{"object": "embedding", "index": i, "embedding": embed(text)}
                                      for i, text in enumerate(inputs)]})
            return
        if not self.path.rstrip("/").endswith("/chat/completions"):
            self._json(404, {"error": {"message": "not found"}})
            return
        request = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))) or b"{}")
        messages = request.get("messages", [])
        reply = reply_for(messages)
        if LOG:
            with _log_lock, open(LOG, "a", encoding="utf-8") as log:
                log.write(json.dumps({"t": time.time(), "messages": messages, "reply": reply}) + "\n")
        created = int(time.time())
        if not request.get("stream"):
            self._json(200, {"id": "stub", "object": "chat.completion", "created": created, "model": MODEL,
                             "choices": [{"index": 0, "finish_reason": "stop",
                                          "message": {"role": "assistant", "content": reply}}],
                             "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2}})
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "close")
        self.end_headers()

        def send(delta: dict, finish: str | None = None) -> None:
            chunk = {"id": "stub", "object": "chat.completion.chunk", "created": created, "model": MODEL,
                     "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}
            self.wfile.write(f"data: {json.dumps(chunk)}\n\n".encode())
            self.wfile.flush()

        send({"role": "assistant", "content": ""})
        for word in reply.split(" "):
            send({"content": word + " "})
        send({}, "stop")
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()
        self.close_connection = True

    def log_message(self, *_):
        pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8000
    print(f"llm stub listening on {port}", flush=True)
    ThreadingHTTPServer(("0.0.0.0", port), Handler).serve_forever()
