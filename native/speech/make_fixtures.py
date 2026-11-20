"""Synthesises the spoken test utterances with Kokoro, as 16 kHz mono PCM16 WAV files with silence around them.

Runs inside the speech server image (kokoro, soxr and numpy are there). Usage: python3 make_fixtures.py <output-dir>
"""
from __future__ import annotations

import sys
import wave
from pathlib import Path

import numpy as np
import soxr
from kokoro import KPipeline

UTTERANCES = {
    "hello": "Hello robot, how are you today?",
    "work": "Where do I work?",
    "style": "How should you answer me?",
}


def main(output: Path) -> None:
    output.mkdir(parents=True, exist_ok=True)
    pipeline = KPipeline(lang_code="a")
    for name, text in UTTERANCES.items():
        audio = np.concatenate([np.asarray(chunk, dtype=np.float32) for _, _, chunk in pipeline(text, voice="af_heart")])
        audio = soxr.resample(audio, 24000, 16000)
        pad = np.zeros(int(0.3 * 16000), dtype=np.float32)
        pcm = (np.clip(np.concatenate([pad, audio, pad]), -1.0, 1.0) * 32767).astype(np.int16)
        with wave.open(str(output / f"{name}.wav"), "wb") as target:
            target.setnchannels(1)
            target.setsampwidth(2)
            target.setframerate(16000)
            target.writeframes(pcm.tobytes())
        print(f"{name}.wav  {len(pcm) / 16000:.2f} s  {text}")


if __name__ == "__main__":
    main(Path(sys.argv[1]))
