# The screencast — what exists, and what still needs a human

## What exists and works right now

`screencast.txt` — a **real captured transcript** of a full run against a live host and a live Neo4j:
preflight, the six-beat demo, and the notebook executed end to end. Replay it with:

```bash
python crosslang/demo/kit/screencast.py          # typed cadence, reads as a live session
python crosslang/demo/kit/screencast.py --fast   # instant, for checking
```

**The replay needs nothing.** No host, no Neo4j, no network, no packages beyond the standard library.
That is deliberate and it is the whole value: Fallback A is reached precisely when the environment is
the thing that failed, so the fallback must not depend on the environment.

Re-record after any change to the demo, against a live host:

```bash
python crosslang/demo/kit/screencast.py --record
```

Recording **aborts and writes nothing** if any step exits non-zero. A fallback recording of a broken
run would hand the room a confident-looking failure at the exact moment nothing else is working.

## What still needs a human: the video

A video file has not been produced, and cannot be produced from here — it needs someone to press
record. This is a **known gap in D3**, stated rather than glossed.

Make it from the replay, not from a live run. Two reasons: the replay cannot fail mid-take, and a
video made from the same transcript can never drift from what the terminal actually printed.

```bash
# 1. a clean, large terminal — 120x40 or wider, high-contrast theme, font large enough
#    to read from the back of a room (16pt+)
# 2. start the screen recorder (OBS, or Win+G on Windows)
# 3. run:
python crosslang/demo/kit/screencast.py
# 4. stop recording; save as crosslang/demo/kit/screencast.mp4  (gitignored — it is a binary)
```

Roughly 3 minutes at replay cadence. Do not narrate the recording: it is the *catastrophic* fallback,
played while you talk over it live, so a second voice track fights you.

**Checklist before calling the video done**

- [ ] Readable at the size it will be projected, not at the size it was recorded
- [ ] The `PROTOTYPE` line in the preflight output is legible — the room must be able to see what this is
- [ ] The three `as_of` lines are on screen together
- [ ] The provenance walk's `✗ closed` row is visible; that is the frame worth pausing on
- [ ] No paths, tokens, or hostnames on screen that shouldn't leave the room
