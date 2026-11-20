"""G-R1 (library part): explicit writes through the journal, on the target image, against a real Neo4j and a stub model.

Standard library only. The stub model (chat completions, the update judge, embeddings) runs in this process; crash
cases run the library in a child process and kill it. Each test gets its own state directory. These tests write to the
store, so they run on their own store (native/gate/run-write-gate.sh), never inside the read-only gate.
Environment: AGENTMEMORY_NATIVE_LIBRARY, AMP_URI, AMP_USER, AMP_PASSWORD, PYTHONPATH (the binding).
"""
from __future__ import annotations

import hashlib
import http.server
import json
import math
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import uuid
from pathlib import Path

from agentmemory_native import MemoryClient, OpenError
from agentmemory_native import _library as abi

DIMENSIONS = 8
OWNER = "gate-writer-owner"
ERASED_OWNER = "gate-erased-owner"
SAID = "2026-11-20T10:00:00Z"


def letters(text: str) -> list[float]:
    """Deterministic vectors from letter counts, the same rule as the native write probe."""
    vector = [0.0] * DIMENSIONS
    for c in text.lower():
        if "a" <= c <= "z":
            vector[(ord(c) - ord("a")) % DIMENSIONS] += 1.0
    norm = math.sqrt(sum(v * v for v in vector))
    return [v / norm for v in vector] if norm else [1.0] + [0.0] * (DIMENSIONS - 1)


class StubModel(http.server.BaseHTTPRequestHandler):
    """An OpenAI-compatible model: the writer's operations, the judge's verdicts and embeddings.

    ``mode`` is "ok" (the writer keeps the last message as one fact, or as a preference when it says "prefer"),
    "fail" (every chat call answers 500), or "hold" (chat calls wait until ``release`` is set, then answer as "ok").
    """

    mode = "ok"
    release = threading.Event()
    lock = threading.Lock()
    writer_calls = 0
    judge_calls = 0
    seen: list[str] = []  # the last message of every writer call
    reasoning: list = []  # the reasoning_effort of every writer call (None when not sent)
    prompts: list[str] = []  # the whole user prompt of every writer call (context included)

    def do_POST(self):  # noqa: N802 - the http.server naming
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        if self.path.endswith("/embeddings"):
            # Text about a zebra gets a vector of the wrong size: the store cannot embed it (the verification test).
            self.answer({"data": [{"index": i, "embedding": [1.0, 0.0, 0.0] if "zebra" in t.lower() else letters(t)}
                                  for i, t in enumerate(body["input"])]})
            return
        system = body["messages"][0]["content"]
        if system.startswith("You keep a person's long-term memory"):
            with StubModel.lock:
                StubModel.judge_calls += 1
            self.reply('{"p1": "no"}')
            return
        with StubModel.lock:
            StubModel.writer_calls += 1
            StubModel.reasoning.append(body.get("reasoning_effort"))
            StubModel.prompts.append(body["messages"][-1]["content"])
        if StubModel.mode == "fail":
            self.send_response(500)
            self.end_headers()
            return
        if StubModel.mode == "hold":
            StubModel.release.wait(120)
        context = body["messages"][-1]["content"]
        said = next(line.split("(LAST MESSAGE): ", 1)[1] for line in context.splitlines() if "(LAST MESSAGE): " in line)
        with StubModel.lock:
            StubModel.seen.append(said)
        words = said.rstrip(".")
        operation = (
            {"op": "add", "kind": "preference", "text": words, "quote": said}
            if "prefer" in said
            else {"op": "add", "kind": "fact", "text": f"user | said | {words}", "quote": said}
        )
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
            pass  # a held call whose caller was killed (the crash tests)

    def log_message(self, *_):
        pass


SERVER = http.server.ThreadingHTTPServer(("127.0.0.1", 0), StubModel)
threading.Thread(target=SERVER.serve_forever, daemon=True).start()
BASE = f"http://127.0.0.1:{SERVER.server_address[1]}"


def config(state: str, *, uri: str | None = None, writer_timeout_ms: int = 30_000, profile: str = "explicit_write",
           owner_mode: str = "fixed", owner: str = OWNER) -> dict:
    document = {
        "schemaVersion": 1,
        "profile": profile,
        "owner": {"mode": "fixed", "id": owner} if owner_mode == "fixed" else {"mode": owner_mode},
        "store": {"uri": uri or os.environ["AMP_URI"], "username": os.environ["AMP_USER"],
                  "password": os.environ["AMP_PASSWORD"]},
        "embedding": {"dimensions": DIMENSIONS, "model": "stub-embedder",
                      "endpoint": {"url": f"{BASE}/v1/embeddings", "timeoutMs": 5000}},
        "writer": {"model": "stub-writer",
                   "endpoint": {"url": f"{BASE}/v1/chat/completions", "timeoutMs": writer_timeout_ms}},
        "state": {"directory": state},
        "limits": {"defaultTimeoutMs": 20_000},
    }
    return document


def new_state() -> str:
    return str(Path(tempfile.mkdtemp(prefix="am-state-")) / "state")


def source(tag: str) -> str:
    return f"gate/{tag}/{uuid.uuid4().hex}"


def journal_line(record: dict) -> bytes:
    data = json.dumps(record, separators=(",", ":")).encode()
    return hashlib.sha256(data).hexdigest().encode() + b" " + data + b"\n"


CHILD = r"""
import json, os, sys, time
from agentmemory_native import MemoryClient
memory = MemoryClient(json.loads(os.environ["GATE_CONFIG"]))
print("open", flush=True)
if os.environ.get("GATE_TEXT"):
    result = memory.remember(source_id=os.environ["GATE_SOURCE"], session_id="crash", text=os.environ["GATE_TEXT"],
                             occurred_at="2026-11-20T10:00:00Z", timeout_ms=1500)
    print(json.dumps({"status": result.status, "state": result.receipt.state if result.receipt else None,
                      "operationId": result.receipt.operation_id if result.receipt else None}), flush=True)
time.sleep(600)
"""


def child(document: dict, text: str | None = None, source_id: str | None = None) -> subprocess.Popen:
    environment = dict(os.environ, GATE_CONFIG=json.dumps(document), GATE_TEXT=text or "", GATE_SOURCE=source_id or "")
    return subprocess.Popen([sys.executable, "-c", CHILD], env=environment, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, text=True)


def wait_until(predicate, timeout_s: float = 20.0) -> bool:
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(0.05)
    return False


class Test1Contract(unittest.TestCase):
    def setUp(self):
        StubModel.mode = "ok"

    def test_capabilities_name_the_profile_its_operations_and_the_writer(self):
        with MemoryClient(config(new_state())) as memory:
            caps = memory.capabilities()
            state = memory.state()
        self.assertEqual(caps["profile"], "explicit_write")
        self.assertEqual(caps["operations"], ["capabilities_get", "memory_get_context", "memory_remember",
                                              "memory_operation_get", "memory_operation_resolve", "memory_forget",
                                              "memory_erase_owner"])
        self.assertEqual(caps["writer"], {"model": "stub-writer", "entityIdentity": False})
        self.assertNotIn(BASE, json.dumps(caps))
        self.assertTrue(state.read_allowed and state.admission_allowed and state.write_quiescent)

    def test_the_read_only_profile_offers_no_write(self):
        document = config(new_state(), profile="read_only")
        del document["writer"], document["state"]
        with MemoryClient(document) as memory:
            refused = memory.remember(source_id=source("ro"), session_id="s", text="I like tea.", occurred_at=SAID)
        self.assertEqual((refused.status, refused.error.code), ("invalid", "unknown_operation"))
        document["writer"] = {"model": "x", "endpoint": {"url": f"{BASE}/v1/chat/completions"}}
        with self.assertRaises(OpenError) as wrong:
            MemoryClient(document)
        self.assertEqual(wrong.exception.code, "not_in_profile")

    def test_configurations_the_profile_cannot_serve_are_refused(self):
        with self.assertRaises(OpenError) as per_request:
            MemoryClient(config(new_state(), owner_mode="per_request"))
        self.assertEqual(per_request.exception.code, "owner_mode_not_offered")
        missing = config(new_state())
        del missing["writer"]
        with self.assertRaises(OpenError) as no_writer:
            MemoryClient(missing)
        self.assertEqual(no_writer.exception.code, "missing_property")
        relative = config("relative/state")
        with self.assertRaises(OpenError) as not_absolute:
            MemoryClient(relative)
        self.assertEqual(not_absolute.exception.code, "invalid_value")

    def test_requests_are_checked_strictly(self):
        with MemoryClient(config(new_state())) as memory:
            no_offset = memory.remember(source_id=source("t"), session_id="s", text="x", occurred_at="2026-11-20T10:00:00")
            named = memory.call({"schemaVersion": 1, "operation": "memory_remember", "sourceId": source("t"),
                                 "sessionId": "s", "text": "x", "occurredAt": SAID, "owner": "someone-else"})
            neither = memory.call({"schemaVersion": 1, "operation": "memory_operation_get"})
            unknown = memory.operation(operation_id="op-missing")
        self.assertEqual((no_offset.status, no_offset.error.code), ("invalid", "invalid_value"))
        self.assertEqual((named["status"], named["error"]["code"]), ("invalid", "owner_not_allowed"))
        self.assertEqual((neither["status"], neither["error"]["code"]), ("invalid", "operation_required"))
        self.assertEqual((unknown.status, unknown.error.code), ("invalid", "operation_not_found"))


class Test2Remember(unittest.TestCase):
    def setUp(self):
        StubModel.mode = "ok"

    def test_applied_means_verified_and_recall_finds_it(self):
        with MemoryClient(config(new_state())) as memory:
            result = memory.remember(source_id=source("apply"), session_id="s1", text="I play the cello every Sunday.",
                                     occurred_at=SAID)
            receipt = result.receipt
            self.assertEqual(result.status, "ok", result.raw)
            self.assertEqual(receipt.state, "applied", receipt)
            self.assertTrue(receipt.remembered)
            self.assertEqual(len(receipt.facts), 1)
            self.assertFalse(receipt.duplicate)
            state = memory.state()
            self.assertTrue(state.read_allowed and state.write_quiescent)
            recalled = memory.get_context(session_id="s1", query="cello every Sunday", max_facts=50, min_score=0.0)
            self.assertEqual(recalled.status, "ok")
            self.assertIn(receipt.facts[0], {f.id for f in recalled.facts})
            again = memory.operation(operation_id=receipt.operation_id)
            self.assertEqual(again.receipt.state, "applied")
            by_source = memory.operation(source_id=receipt.source_id)
            self.assertEqual(by_source.receipt.operation_id, receipt.operation_id)

    def test_duplicate_source_returns_the_same_receipt_and_a_changed_payload_is_refused(self):
        sid = source("dup")
        with MemoryClient(config(new_state())) as memory:
            first = memory.remember(source_id=sid, session_id="s", text="My bicycle is green.", occurred_at=SAID)
            calls = StubModel.writer_calls
            second = memory.remember(source_id=sid, session_id="s", text="My bicycle is green.", occurred_at=SAID)
            conflict = memory.remember(source_id=sid, session_id="s", text="My bicycle is red.", occurred_at=SAID)
        self.assertEqual(first.receipt.state, "applied")
        self.assertTrue(second.receipt.duplicate)
        self.assertEqual(second.receipt.operation_id, first.receipt.operation_id)
        self.assertEqual(StubModel.writer_calls, calls, "a duplicate ran the writer again")
        self.assertEqual((conflict.status, conflict.error.code), ("invalid", "source_conflict"))

    def test_a_model_that_always_fails_is_rejected_and_reads_reopen(self):
        StubModel.mode = "fail"
        with MemoryClient(config(new_state())) as memory:
            result = memory.remember(source_id=source("fail"), session_id="s", text="I collect old maps.",
                                     occurred_at=SAID)
            state = memory.state()
        self.assertEqual(result.receipt.state, "rejected", result.receipt)
        self.assertFalse(result.receipt.searchable)
        self.assertEqual(result.receipt.facts, ())
        self.assertIsNotNone(result.receipt.reason)
        self.assertTrue(state.read_allowed and state.write_quiescent)

    def test_memories_the_store_cannot_embed_are_partial_not_applied(self):
        with MemoryClient(config(new_state())) as memory:
            result = memory.remember(source_id=source("zebra"), session_id="s", text="My zebra is called Stripes.",
                                     occurred_at=SAID)
            receipt = result.receipt
            self.assertEqual(receipt.state, "partial", receipt)
            self.assertTrue(receipt.reason.startswith("not_verified"), receipt.reason)
            self.assertFalse(receipt.searchable)
            self.assertFalse(memory.state().read_allowed, "an unverified write must pause recall")
            self.assertEqual(memory.resolve(operation_id=receipt.operation_id).receipt.state, "kept")
            self.assertTrue(memory.state().read_allowed)

    def test_recall_is_paused_while_a_write_runs_and_finds_it_after(self):
        StubModel.mode = "hold"
        StubModel.release.clear()
        memory = MemoryClient(config(new_state()))
        try:
            first = memory.remember(source_id=source("hold"), session_id="s", text="I prefer tea to coffee.",
                                    occurred_at=SAID, timeout_ms=500)
            self.assertEqual(first.status, "ok")
            self.assertEqual(first.receipt.state, "running", "a job past its deadline still returns the receipt")
            state = memory.state()
            self.assertFalse(state.read_allowed)
            self.assertFalse(state.write_quiescent)
            paused = memory.get_context(session_id="s", query="tea", min_score=0.0)
            self.assertEqual((paused.status, paused.error.code), ("unavailable", "reads_paused"))
            StubModel.release.set()
            self.assertTrue(wait_until(lambda: memory.operation(operation_id=first.receipt.operation_id)
                                       .receipt.state == "applied"))
            after = memory.operation(operation_id=first.receipt.operation_id).receipt
            self.assertEqual(len(after.preferences), 1)
            recalled = memory.get_context(session_id="s", query="tea to coffee", max_preferences=50, min_score=0.0)
            self.assertIn(after.preferences[0], {p.id for p in recalled.preferences})
        finally:
            # The held call is released before the close: closing waits for a running write, so a failed assertion
            # above must not leave this context open for the tests after it.
            StubModel.mode = "ok"
            StubModel.release.set()
            memory.close(timeout_s=60)


class Test3Journal(unittest.TestCase):
    def setUp(self):
        StubModel.mode = "ok"

    def test_accepted_but_never_started_runs_after_the_next_open(self):
        state = Path(new_state())
        state.mkdir(parents=True)
        operation = "op-" + uuid.uuid4().hex
        sid = source("resume")
        (state / "journal.jsonl").write_bytes(journal_line({
            "kind": "accepted", "operationId": operation, "at": SAID, "sourceId": sid, "sourceSha256": "x" * 64,
            "owner": OWNER, "sessionId": "s", "text": "I keep bees on the roof.", "occurredAt": SAID}))
        with MemoryClient(config(str(state))) as memory:
            self.assertTrue(wait_until(lambda: memory.operation(operation_id=operation).receipt.state == "applied"))
            receipt = memory.operation(operation_id=operation).receipt
        self.assertTrue(receipt.remembered)
        kinds = [json.loads(line.split(b" ", 1)[1])["kind"] for line in (state / "journal.jsonl").read_bytes().splitlines()]
        self.assertEqual(kinds, ["accepted", "running", "applied"])

    def test_a_torn_last_line_is_dropped_and_a_damaged_middle_line_refuses_the_open(self):
        state = Path(new_state())
        with MemoryClient(config(str(state))) as memory:
            memory.remember(source_id=source("torn"), session_id="s", text="I swim in the lake.", occurred_at=SAID)
        journal = state / "journal.jsonl"
        good = journal.read_bytes()
        journal.write_bytes(good + b'0123abcd {"kind":"accep')
        with MemoryClient(config(str(state))) as memory:
            self.assertTrue(memory.state().read_allowed)
        self.assertEqual(journal.read_bytes(), good, "the unfinished line was not cut off")
        damaged = bytearray(good)
        damaged[70] ^= 0x01
        journal.write_bytes(bytes(damaged))
        with self.assertRaises(OpenError) as refused:
            MemoryClient(config(str(state)))
        self.assertEqual(refused.exception.code, "journal_corrupt")

    def test_a_second_open_of_the_state_directory_is_busy(self):
        state = new_state()
        other_scheme = os.environ["AMP_URI"].replace("bolt://", "neo4j://")
        with MemoryClient(config(state)):
            with self.assertRaises(OpenError) as same_process:
                MemoryClient(config(state, uri=other_scheme))
        self.assertEqual((same_process.exception.status, same_process.exception.code), (abi.BUSY, "state_locked"))
        holder = child(config(state))
        try:
            self.assertEqual(holder.stdout.readline().strip(), "open")
            with self.assertRaises(OpenError) as other_process:
                MemoryClient(config(state))
            self.assertEqual((other_process.exception.status, other_process.exception.code), (abi.BUSY, "state_locked"))
        finally:
            holder.kill()
            holder.communicate()
        MemoryClient(config(state)).close()

    def test_a_write_running_when_the_process_dies_is_unknown_and_never_rerun(self):
        state = new_state()
        StubModel.mode = "hold"
        StubModel.release.clear()
        sid = source("crash")
        dying = child(config(state), text="I sleep with the window open.", source_id=sid)
        try:
            self.assertEqual(dying.stdout.readline().strip(), "open")
            receipt = json.loads(dying.stdout.readline())
            self.assertEqual(receipt["state"], "running")
            dying.kill()
            dying.wait()
            calls = StubModel.writer_calls
            with MemoryClient(config(state)) as memory:
                found = memory.operation(source_id=sid).receipt
                self.assertEqual((found.state, found.reason), ("unknown", "process_ended_while_running"))
                state_now = memory.state()
                self.assertFalse(state_now.read_allowed)
                paused = memory.get_context(session_id="s", query="window", min_score=0.0)
                self.assertEqual(paused.error.code, "reads_paused")
                time.sleep(1.0)
                self.assertEqual(StubModel.writer_calls, calls, "an unknown write ran again")
                again = memory.remember(source_id=sid, session_id="crash", text="I sleep with the window open.",
                                        occurred_at=SAID)
                self.assertTrue(again.receipt.duplicate)
                self.assertEqual(again.receipt.state, "unknown")
                kept = memory.resolve(operation_id=found.operation_id)
                self.assertEqual(kept.receipt.state, "kept")
                self.assertTrue(memory.state().read_allowed)
                self.assertTrue(memory.get_context(session_id="s", query="window", min_score=0.0).ok)
        finally:
            StubModel.mode = "ok"
            StubModel.release.set()
            if dying.poll() is None:
                dying.kill()
            dying.communicate()

    def test_the_journal_is_private_and_logs_hold_no_memory_text(self):
        state = new_state()
        words = "My locker code is behind the blue door."
        talker = child(config(state), text=words, source_id=source("quiet"))
        try:
            self.assertEqual(talker.stdout.readline().strip(), "open")
            self.assertIn('"state": "applied"', talker.stdout.readline())
        finally:
            talker.kill()
            _, errors = talker.communicate()
        self.assertNotIn("locker", errors)
        self.assertEqual(os.stat(state).st_mode & 0o777, 0o700)
        self.assertEqual(os.stat(Path(state) / "journal.jsonl").st_mode & 0o777, 0o600)



def recalled_ids(memory: MemoryClient, query: str) -> set[str]:
    result = memory.get_context(session_id="s", query=query, max_facts=50, max_preferences=50, min_score=0.0)
    assert result.ok, result.raw
    return {f.id for f in result.facts} | {p.id for p in result.preferences}


class Test4Privacy(unittest.TestCase):
    def setUp(self):
        StubModel.mode = "ok"
        StubModel.release.set()

    def test_a_forgotten_remember_is_no_longer_recalled_and_another_still_is(self):
        with MemoryClient(config(new_state())) as memory:
            oboe = memory.remember(source_id=source("oboe"), session_id="s", text="I play the oboe on Mondays.",
                                   occurred_at=SAID).receipt
            tomato = memory.remember(source_id=source("tomato"), session_id="s", text="I grow tomatoes on the balcony.",
                                     occurred_at=SAID).receipt
            self.assertTrue(oboe.remembered and tomato.remembered)
            forget = memory.forget(operation_id=oboe.operation_id)
            self.assertEqual((forget.status, forget.receipt.kind, forget.receipt.state), ("ok", "forget", "applied"))
            self.assertEqual(set(forget.receipt.closed), set(oboe.facts))
            self.assertEqual(memory.operation(operation_id=oboe.operation_id).receipt.state, "forgotten")
            found = recalled_ids(memory, "oboe on Mondays tomatoes on the balcony")
            self.assertFalse(set(oboe.facts) & found, "a forgotten memory was recalled")
            self.assertTrue(set(tomato.facts) <= found)
            by_id = memory.forget(memory_ids=list(tomato.facts) + ["no-such-memory"])
            self.assertEqual(by_id.receipt.state, "applied")
            self.assertEqual(by_id.receipt.closed, tomato.facts)
            self.assertIn("not_found: no-such-memory", by_id.receipt.reason)
            self.assertFalse(set(tomato.facts) & recalled_ids(memory, "tomatoes on the balcony"))

    def test_a_forget_behind_a_running_remember_waits_and_then_closes_what_it_wrote(self):
        StubModel.mode = "hold"
        StubModel.release.clear()
        memory = MemoryClient(config(new_state()))
        try:
            violin = memory.remember(source_id=source("violin"), session_id="s", text="I tune my violin every morning.",
                                     occurred_at=SAID, timeout_ms=300).receipt
            self.assertEqual(violin.state, "running")
            forget = memory.forget(operation_id=violin.operation_id, timeout_ms=300).receipt
            self.assertEqual((forget.kind, forget.state), ("forget", "accepted"))
            StubModel.mode = "ok"
            StubModel.release.set()
            self.assertTrue(wait_until(lambda: memory.operation(operation_id=forget.operation_id).receipt.state == "applied"))
            ended = memory.operation(operation_id=violin.operation_id).receipt
            self.assertEqual(ended.state, "forgotten")
            self.assertEqual(set(memory.operation(operation_id=forget.operation_id).receipt.closed), set(ended.facts))
            self.assertFalse(set(ended.facts) & recalled_ids(memory, "tune my violin every morning"))
        finally:
            StubModel.mode = "ok"
            StubModel.release.set()
            memory.close(timeout_s=60)

    def test_forgetting_a_remember_that_has_not_started_cancels_it(self):
        StubModel.mode = "hold"
        StubModel.release.clear()
        memory = MemoryClient(config(new_state()))
        try:
            memory.remember(source_id=source("first"), session_id="s", text="I read before sleeping.", occurred_at=SAID,
                            timeout_ms=300)
            second = memory.remember(source_id=source("second"), session_id="s", text="My cat hides under the stairs.",
                                     occurred_at=SAID, timeout_ms=300).receipt
            self.assertEqual(second.state, "accepted")
            memory.forget(operation_id=second.operation_id, timeout_ms=300)
            self.assertEqual(memory.operation(operation_id=second.operation_id).receipt.state, "cancelled")
            StubModel.mode = "ok"
            StubModel.release.set()
            self.assertTrue(wait_until(lambda: memory.state().write_quiescent))
            self.assertNotIn("My cat hides under the stairs.", StubModel.seen, "a cancelled remember reached the writer")
        finally:
            StubModel.mode = "ok"
            StubModel.release.set()
            memory.close(timeout_s=60)

    def test_an_unknown_remember_can_be_forgotten_and_recall_resumes(self):
        state = new_state()
        StubModel.mode = "hold"
        StubModel.release.clear()
        sid = source("unknown-forget")
        dying = child(config(state), text="I keep my passport in the drawer.", source_id=sid)
        try:
            self.assertEqual(dying.stdout.readline().strip(), "open")
            self.assertEqual(json.loads(dying.stdout.readline())["state"], "running")
            dying.kill()
            dying.wait()
            StubModel.mode = "ok"
            StubModel.release.set()
            with MemoryClient(config(state)) as memory:
                unknown = memory.operation(source_id=sid).receipt
                self.assertEqual(unknown.state, "unknown")
                self.assertFalse(memory.state().read_allowed)
                forget = memory.forget(operation_id=unknown.operation_id).receipt
                self.assertEqual(forget.state, "applied")
                self.assertEqual(memory.operation(operation_id=unknown.operation_id).receipt.state, "forgotten")
                self.assertTrue(memory.state().read_allowed)
        finally:
            StubModel.mode = "ok"
            StubModel.release.set()
            if dying.poll() is None:
                dying.kill()
            dying.communicate()

    def test_erase_needs_confirmation_and_waits_for_quiescence(self):
        StubModel.mode = "hold"
        StubModel.release.clear()
        memory = MemoryClient(config(new_state(), owner=ERASED_OWNER))
        try:
            unconfirmed = memory.erase_owner(confirm=False)
            self.assertEqual((unconfirmed.status, unconfirmed.error.code), ("invalid", "confirmation_required"))
            memory.remember(source_id=source("pending"), session_id="s", text="I water the ferns on Fridays.",
                            occurred_at=SAID, timeout_ms=300)
            refused = memory.erase_owner(confirm=True)
            self.assertEqual((refused.status, refused.error.code), ("unavailable", "writes_pending"))
        finally:
            StubModel.mode = "ok"
            StubModel.release.set()
            memory.close(timeout_s=60)

    def test_erase_deletes_everything_of_the_owner_purges_the_journal_and_nothing_comes_back(self):
        # Another owner's memory, which must survive.
        with MemoryClient(config(new_state())) as other:
            survivor = other.remember(source_id=source("survivor"), session_id="s", text="I speak Portuguese at home.",
                                      occurred_at=SAID).receipt
            self.assertTrue(survivor.remembered)
        state = Path(new_state())
        words = ["My bank is the one by the station.", "I prefer window seats on trains."]
        sources = [source("erase-a"), source("erase-b")]
        with MemoryClient(config(str(state), owner=ERASED_OWNER)) as memory:
            for sid, text in zip(sources, words):
                self.assertTrue(memory.remember(source_id=sid, session_id="s", text=text, occurred_at=SAID).receipt.remembered)
            self.assertTrue(recalled_ids(memory, "bank by the station window seats on trains"))
            erase = memory.erase_owner(confirm=True)
            receipt = erase.receipt
            self.assertEqual((erase.status, receipt.kind, receipt.state), ("ok", "erase", "applied"), erase.raw)
            self.assertGreaterEqual(receipt.deleted, 4)  # two memories, two messages, a conversation at least
            self.assertEqual(recalled_ids(memory, "bank by the station window seats on trains"), set())
            self.assertEqual(memory.state().deletion_epoch, 1)
            self.assertTrue(memory.state().read_allowed)
        journal = (state / "journal.jsonl").read_bytes().decode()
        for text in words:
            self.assertNotIn(text, journal, "an erased owner's words stayed in the journal")
        calls = StubModel.writer_calls
        with MemoryClient(config(str(state), owner=ERASED_OWNER)) as memory:
            again = memory.remember(source_id=sources[0], session_id="s", text=words[0], occurred_at=SAID).receipt
            self.assertEqual((again.duplicate, again.state), (True, "erased"))
            self.assertEqual(StubModel.writer_calls, calls, "an erased source ran again")
        with MemoryClient(config(new_state())) as other:
            self.assertIn(survivor.facts[0], recalled_ids(other, "Portuguese at home"))


class Test6Learning(unittest.TestCase):
    """The learning profile (R2): each write applied once by its operation id, with its receipt in the store."""

    def setUp(self):
        StubModel.mode = "ok"
        StubModel.release.set()

    def test_observed_words_are_admitted_only_by_the_learning_profile(self):
        with MemoryClient(config(new_state(), profile="learning")) as memory:
            caps = memory.capabilities()
            observed = memory.remember(source_id=source("observed"), session_id="s", text="My sister lives in Bern.",
                                       occurred_at=SAID, origin="observed")
        self.assertEqual(caps["profile"], "learning")
        self.assertEqual((observed.receipt.state, observed.receipt.origin), ("applied", "observed"))
        self.assertTrue(observed.receipt.remembered)
        with MemoryClient(config(new_state())) as memory:
            refused = memory.remember(source_id=source("observed"), session_id="s", text="My sister lives in Bern.",
                                      occurred_at=SAID, origin="observed")
        self.assertEqual((refused.status, refused.error.code), ("invalid", "not_in_profile"))

    def test_recall_stays_open_while_a_learning_write_runs(self):
        StubModel.mode = "hold"
        StubModel.release.clear()
        memory = MemoryClient(config(new_state(), profile="learning"))
        try:
            pending = memory.remember(source_id=source("open"), session_id="s", text="I fly kites at the beach.",
                                      occurred_at=SAID, timeout_ms=300).receipt
            self.assertEqual(pending.state, "running")
            state = memory.state()
            self.assertTrue(state.read_allowed, "a write applied in one transaction must not pause recall")
            self.assertFalse(state.write_quiescent)
            self.assertTrue(memory.get_context(session_id="s", query="kites", min_score=0.0).ok)
            StubModel.release.set()
            self.assertTrue(wait_until(lambda: memory.operation(operation_id=pending.operation_id).receipt.state == "applied"))
        finally:
            StubModel.mode = "ok"
            StubModel.release.set()
            memory.close(timeout_s=60)

    def test_a_write_killed_while_running_is_applied_once_after_reopening(self):
        state = new_state()
        StubModel.mode = "hold"
        StubModel.release.clear()
        sid = source("learning-crash")
        dying = child(config(state, profile="learning"), text="I grow chillies in pots.", source_id=sid)
        try:
            self.assertEqual(dying.stdout.readline().strip(), "open")
            self.assertEqual(json.loads(dying.stdout.readline())["state"], "running")
            dying.kill()
            dying.wait()
            StubModel.mode = "ok"
            StubModel.release.set()
            with MemoryClient(config(state, profile="learning")) as memory:
                self.assertTrue(wait_until(lambda: memory.operation(source_id=sid).receipt.state == "applied"))
                receipt = memory.operation(source_id=sid).receipt
                self.assertEqual(receipt.reason, None)
                self.assertEqual(len(receipt.facts), 1)
                found = recalled_ids(memory, "grow chillies in pots")
                self.assertTrue(set(receipt.facts) <= found)
        finally:
            StubModel.mode = "ok"
            StubModel.release.set()
            if dying.poll() is None:
                dying.kill()
            dying.communicate()

    def test_a_write_whose_journal_end_was_lost_is_found_by_its_receipt_and_never_runs_again(self):
        state = Path(new_state())
        sid = source("lost-end")
        with MemoryClient(config(str(state), profile="learning")) as memory:
            first = memory.remember(source_id=sid, session_id="s", text="I collect blue stamps.", occurred_at=SAID).receipt
        self.assertEqual(first.state, "applied")
        journal = state / "journal.jsonl"
        lines = journal.read_bytes().splitlines(keepends=True)
        ends = [i for i, line in enumerate(lines)
                if json.loads(line.split(b" ", 1)[1]).get("kind") == "applied"
                and json.loads(line.split(b" ", 1)[1]).get("operationId") == first.operation_id]
        # The commit happened (its receipt is on the message); the journal never heard: as if the process died there.
        journal.write_bytes(b"".join(line for i, line in enumerate(lines) if i not in ends))
        calls = StubModel.writer_calls
        with MemoryClient(config(str(state), profile="learning")) as memory:
            self.assertTrue(wait_until(lambda: memory.operation(source_id=sid).receipt.state == "applied"))
            again = memory.operation(source_id=sid).receipt
        self.assertEqual(StubModel.writer_calls, calls, "a write whose receipt exists ran the writer again")
        self.assertEqual(set(again.facts), set(first.facts))


try:
    import langgraph.store.base  # noqa: F401

    HAS_LANGGRAPH = True
except ImportError:
    HAS_LANGGRAPH = False


class Test7WriterSettings(unittest.TestCase):
    """Optional writer settings, both off unless configured: a reasoning effort (a model that thinks at length before
    answering made voice saves take up to 55 s) and earlier turns of the session as read-only context."""

    def setUp(self):
        StubModel.mode = "ok"
        StubModel.release.set()

    @staticmethod
    def writer(**settings) -> dict:
        document = config(new_state(), profile="learning")
        document["writer"].update(settings)
        return document

    def tell(self, memory, session: str, text: str):
        return memory.remember(source_id=source("told"), session_id=session, text=text, occurred_at=SAID,
                               origin="observed")

    def test_a_reasoning_effort_is_sent_only_when_configured(self):
        for settings, sent in (({}, None), ({"reasoningEffort": "low"}, "low")):
            with self.subTest(settings=settings), MemoryClient(self.writer(**settings)) as memory:
                before = len(StubModel.reasoning)
                told = self.tell(memory, f"effort-{uuid.uuid4().hex}", "I play the cello every Sunday.")
                self.assertEqual(told.receipt.state, "applied", told.raw)
                self.assertEqual(set(StubModel.reasoning[before:]), {sent})

    def test_earlier_turns_reach_the_writer_as_context_and_nothing_is_kept_from_them(self):
        for settings, reaches in (({"contextTurns": 4}, True), ({}, False)):
            session = f"ctx-{uuid.uuid4().hex}"
            with self.subTest(settings=settings), MemoryClient(self.writer(**settings)) as memory:
                first = self.tell(memory, session, "I met somebody at the dinner yesterday.")
                before = len(StubModel.prompts)
                second = self.tell(memory, session, "His name is Sam.")
                self.assertEqual((first.receipt.state, second.receipt.state), ("applied", "applied"))
                self.assertEqual("I met somebody at the dinner yesterday." in StubModel.prompts[before], reaches)
                self.assertEqual(StubModel.seen[-1], "His name is Sam.")
                self.assertEqual(len(second.receipt.facts), 1, "only the turn itself is kept, never its context")

    def test_writer_settings_outside_their_values_are_refused(self):
        for settings in ({"reasoningEffort": "max"}, {"contextTurns": 33}, {"contextTurns": -1}):
            with self.subTest(settings=settings), self.assertRaises(OpenError) as refused:
                MemoryClient(self.writer(**settings))
            self.assertEqual(refused.exception.code, "invalid_value")


@unittest.skipUnless(HAS_LANGGRAPH, "langgraph is not installed (run-write-gate.sh with WRITE_GATE_EXTRAS=langgraph)")
class Test5LangGraph(unittest.TestCase):
    def setUp(self):
        StubModel.mode = "ok"
        StubModel.release.set()
        from agentmemory_native.langgraph import AgentMemoryStore

        self.memory = MemoryClient(config(new_state()))
        self.store = AgentMemoryStore(self.memory, session_id="lg")

    def tearDown(self):
        self.memory.close(timeout_s=60)

    def test_put_remembers_get_returns_the_receipt_and_search_finds_the_memory(self):
        self.store.put(("user", "music"), "instrument", {"text": "I play the harp at weddings."})
        item = self.store.get(("user", "music"), "instrument")
        self.assertIsNotNone(item)
        self.assertTrue(item.value["remembered"])
        self.assertEqual(item.value["state"], "applied")
        hits = self.store.search(("user",), query="harp at weddings", limit=50)
        self.assertTrue(set(item.value["facts"]) <= {hit.key for hit in hits})
        self.assertTrue(all(hit.namespace[0] == "agentmemory" for hit in hits))
        self.assertEqual(self.store.search(("user",)), [], "AgentMemory recalls by meaning; there is no listing")

    def test_the_same_words_again_run_nothing_and_other_words_replace_the_earlier_ones(self):
        self.store.put(("user",), "breakfast", {"text": "I eat porridge for breakfast."})
        first = self.store.get(("user",), "breakfast").value
        calls = StubModel.writer_calls
        self.store.put(("user",), "breakfast", {"text": "I eat porridge for breakfast."})
        self.assertEqual(StubModel.writer_calls, calls, "the same words under the same key ran the writer again")
        self.assertEqual(self.store.get(("user",), "breakfast").value["operation_id"], first["operation_id"])
        self.store.put(("user",), "breakfast", {"content": "I eat pancakes for breakfast."})
        second = self.store.get(("user",), "breakfast").value
        self.assertNotEqual(second["operation_id"], first["operation_id"])
        found = {hit.key for hit in self.store.search(("user",), query="porridge pancakes breakfast", limit=50)}
        self.assertFalse(set(first["facts"]) & found, "the replaced words are still used")
        self.assertTrue(set(second["facts"]) <= found)

    def test_delete_forgets_and_the_key_reads_as_absent(self):
        self.store.put(("user",), "pet", {"text": "My parrot is called Kiwi."})
        facts = self.store.get(("user",), "pet").value["facts"]
        self.store.delete(("user",), "pet")
        self.assertIsNone(self.store.get(("user",), "pet"))
        self.assertFalse(set(facts) & {hit.key for hit in self.store.search(("user",), query="parrot Kiwi", limit=50)})
        # Put again after the delete: a new remember, never the forgotten receipt.
        self.store.put(("user",), "pet", {"text": "My parrot is called Kiwi."})
        again = self.store.get(("user",), "pet")
        self.assertIsNotNone(again)
        self.assertTrue(again.value["remembered"])

    def test_words_are_required_and_the_async_api_works(self):
        with self.assertRaises(ValueError):
            self.store.put(("user",), "empty", {"note": "no text"})

        async def run():
            await self.store.aput(("user",), "city", {"text": "I was born in Valencia."})
            return await self.store.aget(("user",), "city")

        import asyncio

        self.assertTrue(asyncio.run(run()).value["remembered"])


if __name__ == "__main__":
    unittest.main()
