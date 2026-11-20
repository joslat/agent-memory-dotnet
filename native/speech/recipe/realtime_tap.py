"""A tap between a realtime client (the browser voice UI, the Reachy app) and the speech server, for trying the recipe.

Clients connect to the tap instead of the server; every event in both directions is passed through unchanged and
logged as one line: the time, the direction (client>server or server>client), the event type and, for the events
that configure or explain a session (session.*, error, response.done, transcription and transcript events), the
event itself with audio payloads replaced by their size. Audio chunks are counted, never logged.

Usage: python realtime_tap.py <server ws URL, e.g. ws://127.0.0.1:18765> <port> <log file>
"""
from __future__ import annotations

import asyncio
import json
import sys
import time

import websockets

SERVER, PORT, LOG = sys.argv[1].rstrip("/"), int(sys.argv[2]), sys.argv[3]
SHOWN = ("session.", "error", "response.done", "response.created", "conversation.item.input_audio_transcription",
         "response.output_audio_transcript.done", "response.function_call", "conversation.item.create",
         "response.create", "input_audio_buffer.speech", "input_audio_buffer.committed")


def scrub(value):
    if isinstance(value, dict):
        return {k: (f"<{len(v)} base64 chars>" if k in ("audio", "delta") and isinstance(v, str) and len(v) > 64
                    else scrub(v)) for k, v in value.items()}
    if isinstance(value, list):
        return [scrub(v) for v in value]
    if isinstance(value, str) and value.startswith("data:") and len(value) > 64:
        return f"<data URL, {len(value)} chars>"
    return value


def log(direction: str, raw) -> None:
    try:
        event = json.loads(raw)
    except (TypeError, ValueError):
        line = {"t": time.strftime("%H:%M:%S"), "dir": direction, "binary": len(raw)}
    else:
        kind = event.get("type", "?")
        line = {"t": time.strftime("%H:%M:%S"), "dir": direction, "type": kind}
        if kind.startswith(SHOWN) or any(kind.startswith(s) for s in SHOWN):
            line["event"] = scrub(event)
    with open(LOG, "a", encoding="utf-8") as out:
        out.write(json.dumps(line, ensure_ascii=False) + "\n")


async def pipe(source, target, direction: str) -> None:
    audio = 0
    async for message in source:
        if '"input_audio_buffer.append"' in str(message)[:80] or '"response.output_audio.delta"' in str(message)[:80]:
            audio += 1
            if audio % 250 == 1:
                log(direction, json.dumps({"type": f"audio chunks so far: {audio}"}))
        else:
            log(direction, message)
        await target.send(message)


async def handle(client) -> None:
    path = client.request.path if hasattr(client, "request") else "/v1/realtime"
    async with websockets.connect(SERVER + path, max_size=None) as server:
        log("tap", json.dumps({"type": f"client connected, forwarding to {SERVER}{path}"}))
        tasks = [asyncio.create_task(pipe(client, server, "client>server")),
                 asyncio.create_task(pipe(server, client, "server>client"))]
        done, pending = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
        for task in pending:
            task.cancel()
        log("tap", json.dumps({"type": "connection closed"}))


async def main() -> None:
    async with websockets.serve(handle, "127.0.0.1", PORT, max_size=None):
        print(f"realtime tap on ws://127.0.0.1:{PORT} -> {SERVER}", flush=True)
        await asyncio.Future()


if __name__ == "__main__":
    asyncio.run(main())
