"""A scripted, Reachy-shaped client for the speech-to-speech realtime server: no microphone, no robot, no human.

Each turn is either typed text (a user message item, then response.create) or speech: a 16 kHz mono PCM16 WAV streamed
in 20 ms chunks on an absolute schedule, followed by silence until the server ends its response. Every event received
is written to a JSONL trace with a monotonic timestamp (audio payloads replaced by their size), and each turn gets a
summary: transcript, reply, client time from speech end to first audio, and the server's own latency record.

Usage: python3 realtime_client.py <ws-url> <trace.jsonl> <summary.json> turn [turn ...]
       where a turn is "text:<utterance>" or "wav:<path>"
Needs the websockets and numpy packages (both in the speech server's environment).
"""
from __future__ import annotations

import asyncio
import base64
import json
import os
import sys
import time
import wave

import numpy as np
import websockets

RATE = 16000
CHUNK_MS = 20
CHUNK_SAMPLES = RATE * CHUNK_MS // 1000
INSTRUCTIONS = "You are a friendly robot. Answer in one short sentence."
# REALTIME_INSTRUCTIONS_FILE replaces them, for example with remembered facts written into the instructions the way
# the Reachy Mini app's local memory does.
if os.environ.get("REALTIME_INSTRUCTIONS_FILE"):
    with open(os.environ["REALTIME_INSTRUCTIONS_FILE"], encoding="utf-8") as _source:
        INSTRUCTIONS = _source.read().strip()
# How long a turn may wait for the server's response; a turn the server never answers (it can drop an utterance with
# too little speech) ends there as "no_response". REALTIME_TURN_LIMIT_S changes it (tests).
TURN_LIMIT_S = float(os.environ.get("REALTIME_TURN_LIMIT_S", "120"))


def read_wav(path: str) -> np.ndarray:
    with wave.open(path, "rb") as source:
        if source.getframerate() != RATE or source.getnchannels() != 1 or source.getsampwidth() != 2:
            raise ValueError(f"{path}: need 16 kHz mono 16-bit PCM")
        return np.frombuffer(source.readframes(source.getnframes()), dtype=np.int16)


class Session:
    def __init__(self, socket, trace) -> None:
        self.socket = socket
        self.trace = trace
        self.events: asyncio.Queue = asyncio.Queue()

    async def reader(self) -> None:
        async for raw in self.socket:
            now = time.monotonic()
            event = json.loads(raw)
            logged = dict(event)
            if isinstance(logged.get("delta"), str) and logged.get("type", "").endswith("audio.delta"):
                logged["delta"] = f"<{len(event['delta'])} base64 chars>"
            self.trace.write(json.dumps({"t": now, "event": logged}) + "\n")
            await self.events.put((now, event))

    async def send(self, event: dict) -> None:
        await self.socket.send(json.dumps(event))

    async def expect(self, kind: str, limit_s: float = 30.0) -> dict:
        deadline = time.monotonic() + limit_s
        while True:
            _, event = await asyncio.wait_for(self.events.get(), max(0.01, deadline - time.monotonic()))
            if event.get("type") == kind:
                return event
            if event.get("type") == "error":
                raise RuntimeError(f"server error while waiting for {kind}: {event}")


async def stream_silence_until(session: Session, done: asyncio.Event, start: float, index: int) -> int:
    silence = base64.b64encode(np.zeros(CHUNK_SAMPLES, dtype=np.int16).tobytes()).decode()
    while not done.is_set():
        await asyncio.sleep(max(0.0, start + index * CHUNK_MS / 1000 - time.monotonic()))
        await session.send({"type": "input_audio_buffer.append", "audio": silence})
        index += 1
    return index


async def run_turn(session: Session, turn: str) -> dict:
    summary: dict = {"turn": turn if turn.startswith("text:") else turn.split("/")[-1]}
    done = asyncio.Event()
    first_audio: list[float] = []
    collected: dict = {}

    async def watch() -> None:
        # The turn always ends here: answered, refused, or out of time. Before, running out of time raised inside this
        # task, nothing set `done`, and the silence stream below went on forever.
        try:
            await watch_events()
        except asyncio.TimeoutError:
            collected["status"] = "no_response"
        finally:
            done.set()

    async def watch_events() -> None:
        deadline = time.monotonic() + TURN_LIMIT_S
        while not done.is_set():
            now, event = await asyncio.wait_for(session.events.get(), max(0.01, deadline - time.monotonic()))
            kind = event.get("type", "")
            if kind == "conversation.item.input_audio_transcription.completed":
                collected["transcript"] = event.get("transcript")
            elif kind == "response.output_audio.delta" and not first_audio:
                first_audio.append(now)
            elif kind == "response.output_audio_transcript.done":
                collected["reply"] = event.get("transcript")
            elif kind == "response.done":
                response = event.get("response", {})
                collected["status"] = response.get("status")
                record = (response.get("metadata") or {}).get("speech_to_speech.turn_latency")
                collected["server_latency"] = json.loads(record) if record else None
                done.set()
            elif kind == "error":
                collected["error"] = event.get("error")
                done.set()

    watcher = asyncio.create_task(watch())
    if turn.startswith("text:"):
        sent_at = time.monotonic()
        await session.send({"type": "conversation.item.create",
                            "item": {"type": "message", "role": "user",
                                     "content": [{"type": "input_text", "text": turn[5:]}]}})
        await session.send({"type": "response.create"})
        speech_end = sent_at
    else:
        samples = read_wav(turn[4:])
        start = time.monotonic()
        index = 0
        for offset in range(0, len(samples), CHUNK_SAMPLES):
            chunk = samples[offset:offset + CHUNK_SAMPLES]
            if len(chunk) < CHUNK_SAMPLES:
                chunk = np.pad(chunk, (0, CHUNK_SAMPLES - len(chunk)))
            await asyncio.sleep(max(0.0, start + index * CHUNK_MS / 1000 - time.monotonic()))
            await session.send({"type": "input_audio_buffer.append", "audio": base64.b64encode(chunk.tobytes()).decode()})
            index += 1
        speech_end = start + index * CHUNK_MS / 1000
        await stream_silence_until(session, done, start, index)
    await watcher
    summary.update(collected)
    summary["client_first_audio_s"] = round(first_audio[0] - speech_end, 3) if first_audio else None
    return summary


async def main(url: str, trace_path: str, summary_path: str, turns: list[str]) -> None:
    # Line-buffered, so a trace read while the client runs (or after it is killed) shows every event received so far.
    with open(trace_path, "w", encoding="utf-8", buffering=1) as trace:
        # The server releases a closed session's slot asynchronously; a client that connects right after another one
        # left can be refused with session_limit_reached. Retry that for up to 15 s, as a reconnecting robot would.
        for attempt in range(30):
            socket = await websockets.connect(url, max_size=None)
            first = json.loads(await socket.recv())
            if first.get("type") == "error" and (first.get("error") or {}).get("type") == "session_limit_reached":
                await socket.close()
                await asyncio.sleep(0.5)
                continue
            break
        else:
            sys.exit("the server kept refusing the session: all session slots in use")
        async with socket:
            trace.write(json.dumps({"t": time.monotonic(), "event": first, "attempts": attempt + 1}) + "\n")
            session = Session(socket, trace)
            reader = asyncio.create_task(session.reader())
            if first.get("type") != "session.created":
                await session.expect("session.created")
            await session.send({"type": "session.update", "session": {
                "type": "realtime", "instructions": INSTRUCTIONS,
                "extensions": ["speech_to_speech.input_audio_transcription.snapshot"],
                "audio": {"input": {"turn_detection": {"type": "server_vad", "interrupt_response": True}},
                          "output": {}}}})
            await session.expect("session.updated")
            results = [await run_turn(session, turn) for turn in turns]
            reader.cancel()
    with open(summary_path, "w", encoding="utf-8") as out:
        json.dump(results, out, indent=1)
    for result in results:
        latency = result.get("server_latency") or {}
        print(f"{result['turn'][:40]:40} status={result.get('status')} first_audio={result.get('client_first_audio_s')}s "
              f"e2e={latency.get('e2e_s')} transcript={result.get('transcript')!r} reply={result.get('reply')!r}")


if __name__ == "__main__":
    if len(sys.argv) < 5:
        sys.exit(__doc__)
    asyncio.run(main(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:]))
