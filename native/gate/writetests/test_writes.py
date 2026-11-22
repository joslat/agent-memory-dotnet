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
import socketserver
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


class LocalHTTPServer(http.server.ThreadingHTTPServer):
    """A test server on 127.0.0.1, bound without HTTPServer's reverse lookup of its own name (socket.getfqdn): that
    lookup took about 35 s on macOS runners, for each server (NATIVE-PLAT-STORE)."""

    def server_bind(self):
        socketserver.TCPServer.server_bind(self)
        self.server_name, self.server_port = self.server_address[:2]


SERVER = LocalHTTPServer(("127.0.0.1", 0), StubModel)
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


WINDOWS_ACCESS = r"""
$acl = Get-Acl -LiteralPath $env:AM_ACL_PATH
$rules = @($acl.Access | ForEach-Object {
  @{ sid = $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
     type = [string]$_.AccessControlType; rights = [string]$_.FileSystemRights; inherited = $_.IsInherited } })
@{ protected = $acl.AreAccessRulesProtected; me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
   rules = $rules } | ConvertTo-Json -Depth 4 -Compress
"""
SYSTEM_SID = "S-1-5-18"


def windows_access(path: Path) -> dict:
    """Windows: the access list of a file or folder, by security identifier, and who this process runs as."""
    done = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", WINDOWS_ACCESS],
                          env=dict(os.environ, AM_ACL_PATH=str(path)), capture_output=True, text=True, check=True)
    return json.loads(done.stdout)


def wait_until(predicate, timeout_s: float = 20.0) -> bool:
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(0.05)
    return False


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
        if os.name == "nt":
            # The counterpart of 0700 and 0600: the folder grants only this user and SYSTEM, inherits nothing from its
            # parent, and the journal inside inherits exactly that.
            folder = windows_access(Path(state))
            self.assertTrue(folder["protected"], folder)
            self.assertEqual({r["sid"] for r in folder["rules"]}, {folder["me"], SYSTEM_SID}, folder)
            self.assertTrue(all(r["type"] == "Allow" and r["rights"] == "FullControl" and not r["inherited"]
                                for r in folder["rules"]), folder)
            journal = windows_access(Path(state) / "journal.jsonl")
            self.assertEqual({r["sid"] for r in journal["rules"]}, {journal["me"], SYSTEM_SID}, journal)
            self.assertTrue(all(r["type"] == "Allow" and r["inherited"] for r in journal["rules"]), journal)
        else:
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


# NATIVE-GATE-SHARED: the tier C cases every binding passes (native/gate/cases/writes.json).
from shared_write_cases import Test9SharedWriteCases  # noqa: E402,F401


if __name__ == "__main__":
    unittest.main()
