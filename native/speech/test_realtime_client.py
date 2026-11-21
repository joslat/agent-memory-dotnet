"""The scripted client ends a turn the server never answers, instead of streaming silence forever.

A server may drop an utterance without replying: the speech server discards a segment with too little confident speech
("Sure." heard as 352 ms against a 384 ms minimum, benchmark stage 3, 20 November 2026). The client's turn limit must
then end the turn, record it as unanswered and go on; before, the limit fired inside the watcher task, nothing marked
the turn over, and the client sent silence until it was killed (an hour later). This test runs the client against a
stand-in server that accepts the session and never responds, with a 2-second turn limit.

Run: python native/speech/test_realtime_client.py
"""
from __future__ import annotations

import asyncio
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import threading
import unittest
import wave

import websockets

HERE = pathlib.Path(__file__).resolve().parent


def silent_wav(path: pathlib.Path, seconds: float = 0.4) -> None:
    with wave.open(str(path), "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(16000)
        out.writeframes(b"\x00\x00" * int(16000 * seconds))


class SilentServer:
    """Accepts a realtime session and never answers a turn."""

    def __init__(self) -> None:
        self.port = 0
        self._loop = asyncio.new_event_loop()
        self._ready = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)

    async def _handle(self, socket) -> None:
        await socket.send(json.dumps({"type": "session.created", "session": {}}))
        async for raw in socket:
            if json.loads(raw).get("type") == "session.update":
                await socket.send(json.dumps({"type": "session.updated", "session": {}}))

    def _run(self) -> None:
        asyncio.set_event_loop(self._loop)

        async def serve() -> None:
            async with websockets.serve(self._handle, "127.0.0.1", 0) as server:
                self.port = server.sockets[0].getsockname()[1]
                self._ready.set()
                await asyncio.Future()

        self._loop.run_until_complete(serve())

    def start(self) -> "SilentServer":
        self._thread.start()
        self._ready.wait(10)
        return self


class UnansweredTurnTests(unittest.TestCase):
    def test_a_turn_the_server_never_answers_ends_at_the_turn_limit(self) -> None:
        server = SilentServer().start()
        with tempfile.TemporaryDirectory() as work:
            work_dir = pathlib.Path(work)
            silent_wav(work_dir / "turn.wav")
            env = dict(os.environ, REALTIME_TURN_LIMIT_S="2")
            try:
                done = subprocess.run(
                    [sys.executable, str(HERE / "realtime_client.py"), f"ws://127.0.0.1:{server.port}",
                     str(work_dir / "trace.jsonl"), str(work_dir / "summary.json"),
                     f"wav:{work_dir / 'turn.wav'}", f"wav:{work_dir / 'turn.wav'}"],
                    env=env, capture_output=True, text=True, timeout=30)
            except subprocess.TimeoutExpired:
                self.fail("the client never ended the unanswered turn (it streams silence forever)")
            self.assertEqual(done.returncode, 0, done.stderr[-2000:])
            summary = json.loads((work_dir / "summary.json").read_text(encoding="utf-8"))
            self.assertEqual([turn.get("status") for turn in summary], ["no_response", "no_response"],
                             "each unanswered turn is recorded as such, and the next turn still runs")
            self.assertGreater((work_dir / "trace.jsonl").stat().st_size, 0, "the trace is written as events arrive")


if __name__ == "__main__":
    unittest.main()
