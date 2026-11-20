"""A scripted stand-in for the writer model and the update judge, for SPEECH-BENCH's dry run and its oracle-writer arm.

It answers POST /v1/chat/completions (not streamed) from writer-script.json (made by corpus.py):
  - a writer request (its system prompt says "long-term memory of one kind"): the operations scripted for the person's
    LAST MESSAGE, matched loosely (speech recognition may change a word: the scripted line sharing most words, at least
    60%), each quoting the words as heard; a scripted "replaces" becomes a replace naming the STORED item that holds
    those words (an add when none does); anything else gets {"ops": []};
  - an update-judge request ("whether the new memory replaces the stored one"): "yes" for a pair the script replaces,
    "no" for every other pair.
Each answer is logged to stdout as one line: the kind of request and how many operations or pairs (no message text).
Usage: python3 writer_stub.py <writer-script.json> [port]
"""
from __future__ import annotations

import json
import re
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SCRIPT = json.load(open(sys.argv[1], encoding="utf-8"))["utterances"]
REPLACEMENTS: set[tuple[str, str]] = set()  # (new text, words of the stored memory it replaces)
_lock = threading.Lock()


def text_of(content) -> str:
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return " ".join(part.get("text", "") for part in content if isinstance(part, dict))
    return ""


def words(text: str) -> set[str]:
    return set(re.findall(r"[a-z0-9]+", text.lower()))


def scripted(heard: str) -> list[dict]:
    """The scripted line the heard words match: the one sharing most words, if it shares at least 70% of both."""
    best, score = None, 0.0
    for line in SCRIPT:
        a, b = words(heard), words(line)
        overlap = len(a & b) / max(len(a | b), 1)
        if overlap > score:
            best, score = line, overlap
    return SCRIPT[best] if best is not None and score >= 0.6 else []


def writer_answer(prompt: str) -> dict:
    last = re.findall(r"\(LAST MESSAGE\): (.*)", prompt)
    said = last[-1].strip() if last else ""
    stored = dict(re.findall(r"^([FPER]\d+): (.*)$", prompt.split("STORED (live, most relevant):", 1)[-1], re.MULTILINE))
    ops = []
    for op in scripted(said):
        op = dict(op, quote=said)
        replaces = op.pop("replaces", None)
        if replaces is not None:
            label = next((key for key, value in stored.items() if replaces in value), None)
            if label is None:
                op["op"] = "add"
            else:
                op["id"] = label
                with _lock:
                    REPLACEMENTS.add((op["text"], replaces))
        ops.append(op)
    return {"ops": ops}


def judge_answer(prompt: str) -> dict:
    answer = {}
    for key, new, old in re.findall(r'^(p\d+): Does the new memory "(.*)" replace this stored one: "(.*)"\?$', prompt,
                                    re.MULTILINE):
        with _lock:
            answer[key] = "yes" if any(new == n and r in old for n, r in REPLACEMENTS) else "no"
    return answer


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _json(self, status: int, payload: dict) -> None:
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):  # noqa: N802
        request = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))) or b"{}")
        if not self.path.rstrip("/").endswith("/chat/completions"):
            self._json(404, {"error": {"message": "not found"}})
            return
        messages = request.get("messages", [])
        system = " ".join(text_of(m.get("content")) for m in messages if m.get("role") == "system")
        user = "\n".join(text_of(m.get("content")) for m in messages if m.get("role") == "user")
        if "long-term memory of one kind" in system:
            answer, kind = writer_answer(user), "writer"
            size = len(answer["ops"])
        elif "whether the new memory replaces the stored one" in system:
            answer, kind = judge_answer(user), "judge"
            size = len(answer)
        else:
            answer, kind, size = {}, "other", 0
        print(json.dumps({"t": round(time.time(), 3), "kind": kind, "size": size}), flush=True)
        self._json(200, {"id": "writer-stub", "object": "chat.completion", "created": int(time.time()),
                         "model": "writer-stub", "choices": [{"index": 0, "finish_reason": "stop", "message": {
                             "role": "assistant", "content": json.dumps(answer)}}],
                         "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2}})

    def log_message(self, *_):
        pass


if __name__ == "__main__":
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 8001
    print(f"writer stub listening on {port} with {len(SCRIPT)} scripted utterances", flush=True)
    ThreadingHTTPServer(("0.0.0.0", port), Handler).serve_forever()
