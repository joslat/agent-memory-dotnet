# Reachy Mini as a co-speaker: the plan

For José to review before anything is built (waves 54 and 55 of the plan). The co-speaker is an independent demo built
with AgentMemory. Nothing in it presents Reachy as a spokesperson of Hugging Face or Pollen Robotics. Pollen's open
pull requests (reachy_mini_conversation_app #616, huggingface/speech-to-speech #860) and their branches are never
touched.

## 1. What it is

Reachy listens to a talk about Hugging Face, Pollen Robotics and their robots and helps the human speaker gently. Its
character and everything it knows come from loaded ampacks. It speaks only when invited, can be stopped at any time,
and goes back to listening.

Your choices of 25 Nov (docket `cos-modes`, `cos-wake`, `cos-eval`):

- Listening by default.
- Speaking only on a wake phrase or a cued silence.
- Interruptible, and back to listening after.
- Built first as DemoBrain tooling, then on the robot.
- A kill switch.
- One round of evaluation first, 15 questions a pack.
- A configurable silence threshold, tried at 1.5 to 2 s.
- A light shakedown before anything bigger.

## 2. What already exists (read on 25 Nov)

**AgentMemory packs:**

- Signed ampacks: `spec/core.md`, `src/AgentMemory.Packs.Format`, and `tools/Ampack` with `--json` output.
- A knowledge pack holds at most 5,000 records and needs no other pack.
- Packs travel together as a family or a set.

**The mind convention** (`strategy/ampack/mind/conventions/mind-v2.0.md`) already has what this demo needs:

- principles with an *always* flag and a priority
- a stance
- a self-description
- host-selected protocol stages
- tool contracts
- knowledge needs
- behaviour scenarios (`assets/mind-evals.json`)

Examples: the advocate mind and the two coach minds.

**Per-fact sources:** a pack's own convention. The Swiss tutor pack carries `sourceUrl`, `sourcePages` and `asOf`; the
advocate pack carries `sourceKey`.

**DemoBrain** (`demobrain/`, its own repository):

- Voice: `DemoBrain.Voice` has local speech recognition (`LocalSpeech`), Kokoro speech, a `SentenceSplitter`,
  `SpeechPlayback`, a turn that can be interrupted (`VoiceTurn`), and `NameMatcher`, which corrects misheard known
  names.
- Scripted shows (`ShowScript`, `--show shows/*.json`) with `say`, `narrate`, `wait`, `expect` and Cypher `check`
  steps, pack steps, a fresh owner per run, and a pass/fail report.
- Retrieval shown live (the memory router).

**Reachy Mini conversation app** (Pollen's, cloned read-only at `C:\git\joslat\reachy_mini_conversation_app`,
commit 308f836). Its real tools:

| Tool | Parameters |
|---|---|
| `move_head` | direction: left, right, up, down, front |
| `play_emotion` | intent: happy, excited, grateful, thinking, attentive, surprised, amazed, yes, yes_understanding, no, and more |
| `head_tracking` | enabled: true or false (follow the speaker's face) |
| `sweep_look` | none |
| `idle_do_nothing` | reason |
| `stop_emotion`, `stop_dance`, `go_to_sleep`, `volume_control`, `camera`, `remember`, `forget` | as in the app |

The app has its own voice detection and barge-in (`LOCAL_VAD_ENABLED`, `LOCAL_BARGE_IN_ENABLED`). Its rule I12 says
recalled text never authorises robot movement. The plan keeps that rule: actions come from the session pack's allowed
list, chosen by the turn policy.

**Not found anywhere:** detection of being addressed by name, and any pack with a timed talk script.

**Simulator:** `reachy-mini-daemon --sim` (MuJoCo; Windows support experimental) or the lighter `--mockup-sim`.

**AgentEval** (`C:\git\AgentEval`) has:

- repeated runs with confidence intervals (`StochasticRunner`)
- scripted conversations (`ConversationRunner`)
- judge calibration with Cohen's κ (`GateCalibrationHarness`, `CalibratedJudge`)
- JUnit and markdown output

SARIF exists only in the red-team exporter.

## 3. The modes and the turn policy

One small, deterministic component decides; the model only writes Reachy's line. Because the decision is code, "never
interrupts" is proved by unit tests as well as measured.

```
             wake phrase                          sentence ends, or the speaker resumes
LISTENING ───────────────► SPEAKING ─────────────────────────────────────────────► LISTENING
    │   cued moment + silence ≥ threshold ▲            │ kill switch (key or "Reachy, quiet")
    └─────────────────────────────────────┘            ▼
                                                     MUTED ── key or "Reachy, you can talk" ──► LISTENING
```

**Wake phrase.** A name variant counts only as an address, never as a mention:

- **Name variants:** Reachy, Richy, Richie, Ritchie, Richye, plus what tonight's speech session shows the recogniser
  writes. `NameMatcher` corrects near misses.
- **Address forms:** "Reachy, what do you think?", "… what do you say, Reachy?", "Yes, Reachy?", and a name opening or
  closing a question.
- **Mentions:** "Reachy was built by Pollen…" mentions the name in the third person and keeps it silent.

**Cued silence.** The session pack marks Reachy moments. The policy follows the talk's acts from the speaker's cue
phrases. At a Reachy moment, silence of at least the threshold (default 1.8 s; 1.5 to 2 s to try) opens speaking. A
pause at any other moment does not.

**Yield.** Reachy speaks sentence by sentence (`SentenceSplitter`). If the speaker starts talking, it finishes the
current sentence and stops. A later fix in the speech path will make that stop sooner.

**Kill switch:**

- A hotkey in DemoBrain (proposed F9) and the phrase "Reachy, quiet" mute it until released.
- In the robot phase, the same switch sends `stop_emotion` and `idle_do_nothing`.

**Decision log.** One line per decision: time, mode, what was heard, decision (`SPEAK`, `SILENT`, `ACT`) and the reason
(for example "third-person mention", "cued moment, silence 1.9 s", "muted"). Recalls are logged with the pack and the
record that answered.

**Non-verbal.** While listening, Reachy may only use the session's allowed actions at its moments: `head_tracking`
on, `play_emotion` attentive or yes, `move_head` front. In DemoBrain these actions are logged, not sent; on the
robot they go through the app's own tools.

## 4. The packs

A set with the mind at the root (`io.github.joslat.minds.set.cospeaker`). Sources are kept in the workshop
(`strategy/ampack/packs/cospeaker-*`), the samples' one home.

| Pack | Kind | Contents |
|---|---|---|
| `cospeaker-mind` | mind | See below |
| `cospeaker-session-hf-pollen` | knowledge | The talk: acts, cues, Reachy moments, suggested lines, allowed actions, how much Reachy may improvise (section 5) |
| `expert-huggingface` | knowledge | What Hugging Face is and does: the Hub, the open-source libraries, the community, open models |
| `expert-pollen-robotics` | knowledge | Pollen Robotics, its robots, its relationship to Hugging Face |
| `expert-reachy-mini` | knowledge | Reachy Mini (hardware, set-up, options, apps, code repositories) and the small duck robot, whose official name is confirmed first (it may be the Open Duck Mini) |
| `expert-agentmemory` | knowledge | AgentMemory, ampacks, memory types, the mind, abstention, how packs compose, how Reachy's personality is loaded |

**The mind:**

- **Values and principles, refined from your brief:**
  - the speaker leads
  - never interrupt
  - one to three sentences, then stop
  - honest: an in-character "I don't know" when the packs do not hold it
  - warm and lightly funny, never sarcastic
  - stays on topic and hands back to the speaker
  - non-verbal first
- **Self-description:** "built with AgentMemory, created by José Luis Latorre Millás, my personality comes from the
  packs I load", plus one line saying it is an independent demo.
- **Protocol stages:** the modes above.
- **Tool contracts:** the allowed robot actions.
- **Knowledge needs:** the four expert packs.

**Facts:**

- Every fact carries `sourceUrl` and `accessedOn` (and `asOf` where the source dates it).
- Sources are the organisations' own sites, repositories and documentation, fetched for this.
- Nothing is filled from model memory. Dates, headcounts, funding and acquisition details are left out unless a source
  states them.
- Entries are short and speakable.
- Every uncertain fact goes to `docs/cospeaker/FACTS-TO-CONFIRM.md` for you.

Every pack passes `ampack verify` and admission, and the set composes with the mind at the root.

## 5. The session pack

A small talk ontology inside the session pack (types of its own, as the format requires):

- **`Act`:** order, title, what the speaker is expected to say (paraphrase), and its cue phrases.
- **`ReachyMoment`:** its act, its trigger (invitation or cued silence), a suggested line, how much Reachy may
  improvise (none, light, free), and its allowed actions.

The flow, to adjust with you:

1. Opening: the speaker introduces themself and Reachy; Reachy greets when invited.
2. Hugging Face in two minutes.
3. Pollen Robotics and how it relates to Hugging Face.
4. Reachy Mini and the duck robot: what they are, how to set them up, where the code is.
5. The reveal: Reachy explains "what makes me me" (AgentMemory ampacks).
6. Audience Q&A: Reachy answers only when the speaker passes a question to it.
7. Closing: Reachy thanks the audience when invited.

## 6. Evaluation

**The shakedown (wave 54; text only, one round, well under USD 1)** is a DemoBrain show per scenario: the speaker's
lines and silence markers in, Reachy's decisions checked with `expect` steps. It covers:

- false wake (third person)
- uncued silence mid-explanation
- cued silence above the threshold
- an invitation for each expert pack
- an out-of-pack question (an honest "I don't know")
- the speaker resuming while Reachy talks (yield)
- the kill switch
- "what makes you you"

**The AgentEval suite (wave 55):**

- **Scenarios:** your list in full: the whole talk, each act, false wake, uncued and cued silence, invitations,
  out-of-pack, provocative or off-topic questions, yield, and "who made you".
- **Questions:** 15 a pack; one round first, then repeats only where a result needs them.
- **Metrics, proposed gates:**

| Metric | Proposed gate |
|---|---|
| Interruptions | 0 |
| Turn-taking precision and recall | ≥ 0.95 |
| Invitation answered within the latency budget (first word ≤ 1.5 s at p90) | ≥ 0.98 |
| Factual correctness (calibrated judge, lower bound of the interval) | ≥ 0.90 |
| Groundedness and abstention | ≥ 0.95 |
| Persona: brief (≤ 50 words), gentle, on topic | ≥ 0.90 |
| Action validity | 100% |

The memory-layer part is deterministic: which pack and record answered, and whether an out-of-pack question abstained.
The judge is calibrated on a small hand-labelled set first. Output is JUnit plus `docs/cospeaker/EVAL-REPORT.md`, with
every failure, its transcript excerpt and its decision log.

**Trials (wave 55):**

1. The full talk three times: text first, then with speech (synthesised speaker lines, Reachy's normal speech), then the
   simulator or the robot.
2. Each run is recorded under `recordings/cospeaker/<date>-run-<n>/`: transcript, decisions, recalls, actions,
   latencies, a screen and audio capture with DemoBrain's retrieval live, and a `SUMMARY.md`.
3. One 60 to 90 s clip.

## 7. Tasks and order

| Wave | Task | What | Estimate |
|---|---|---|---|
| 54 | COS-PLAN | This plan | done when you approve it |
| 54 | COS-MODES | DemoBrain: modes, turn policy, wake phrases, yield, kill switch, decision log; unit tests for every rule | 1 to 2 days |
| 54 | COS-PACKS | The six packs, sources fetched, FACTS-TO-CONFIRM.md | 1 to 1.5 days |
| 54 | COS-SESSION | The session pack and its talk ontology | 0.5 to 1 day |
| 54 | COS-SHAKEDOWN | The light shakedown shows | 2 to 4 hours |
| 55 | COS-EVAL | The AgentEval suite, judge calibration, report | 1.5 to 2 days |
| 55 | COS-TRIAL | Three recorded runs and the clip (with you; the robot or the simulator) | 1 day |

Wave 54 is about 3 to 4 working days after your approval. It goes first in the order, ahead of everything except the
plan triage and the shared-name design, which I do while you read this. The speech findings of tonight (misheard
words, B31) feed the wake phrases and are fixed before the spoken trials.

## 8. Questions for you

1. **Branch.** Your brief says a new branch; your standing rule is one working branch per repository. I propose:
   - the plan and packs on the current batch branch (`feat/post-276-batch`);
   - DemoBrain's code on its working branch (`feat/post-pr2-batch`);
   - Reachy-side code, only in wave 55, on a branch of your fork of the app.
2. **The kill switch's key:** F9?
3. **Language:** English only for the talk?
4. **The event:** is there a date, a venue or a length for the talk? It sets the session pack and the clip.
5. **Robot or simulator** for the trials: is the real Reachy Mini available?
