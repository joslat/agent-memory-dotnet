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
    "dog": "What is my dog called?",
    "food": "What food do I like?",
    "instrument": "Which instrument do I play?",
    # G-R1: remember, change, forget, by voice.
    "remember_colour": "Please remember that my favourite colour is green.",
    "ask_colour": "What is my favourite colour?",
    "remember_colour_blue": "Please remember that my favourite colour is blue now.",
    "remember_cat": "Please remember that my cat is called Tom.",
    "forget_last": "Forget what I just told you.",
    "ask_cat": "What is my cat called?",
    # G-R2: learning unasked, by voice.
    "tell_sister": "My sister Ana lives in Lisbon.",
    "ask_weather": "What is the weather like today?",
    "ask_sister": "Where does my sister live?",
    "tell_sister_moved": "My sister Ana moved to Madrid last month.",
    "tell_brother": "My brother plays chess every weekend.",
    "ask_brother": "What does my brother play?",
    "tell_tea": "My favourite tea is Earl Grey.",
    "ask_tea": "What is my favourite tea?",
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
