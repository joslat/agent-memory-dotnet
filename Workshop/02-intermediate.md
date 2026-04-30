# Module 2 — Intermediate: Compound the Memory and Inspect the Artifacts

> The point of this module: see whether the second feature goes faster because the repo *remembers*, or whether the team retraces the same steps. Then look inside `.squad/` and judge for yourself.

← Back to [Workshop Index](README.md) · [← Module 1 (Basic)](01-basic.md)

---

## Prerequisites

- You completed [Module 1](01-basic.md) and have a working Reading List app under `reading-list-squad-lab/`.
- The team is already cast, decisions are recorded in `.squad/decisions.md`, and at least one architectural decision was made.
- A Copilot CLI session is running with the Squad agent (`copilot --agent squad`). If you closed it, just relaunch from inside the project directory — you don't need to re-init.

If you closed and came back later:

```powershell
cd reading-list-squad-lab
squad status     # confirm the squad is active
squad doctor     # confirm setup is healthy
copilot --agent squad
```

---

## Step 7: Add a second-wave feature

Now build something slightly annoying:

```
Add these features and update everything accordingly:
1. Filter books by unread/read status (both API and UI)
2. Validation rule: title is required, author is required
3. Update all existing tests and add new ones for the filter and validation
4. Update the UI to show a filter toggle (All / Unread / Read)
```

**What to watch for:**
- Do the agents reuse prior decisions and skills cleanly?
- Does the second feature go faster because the repo memory helped?
- Does the team avoid re-explaining things that were already decided?
- Does the Tester catch the regression risk on the existing tests instead of writing brand new ones in isolation?

If the team structure reduces re-explaining, you will feel the difference here. If you find yourself pasting context the team should already have, that's a real signal — note it.

---

## Step 8: Commit and push

Ask the team to wrap up:

```
Commit all changes with a clear commit message summarizing what was built.
Push to origin.
```

Or do it manually in a separate terminal:

```powershell
git add -A
git commit -m "feat: reading list app with CRUD, filtering, validation, and tests"
git push
```

> Both work. The interesting question is which one *feels* more correct to you. If you trust the team to commit on their own, that's information about how the workshop went.

---

## Step 9: Look inside `.squad/`

Before celebrating, inspect the team's artifacts. Open each file and evaluate whether it looks like real work or pleasant-sounding filler:

```powershell
# Decisions — were they useful?
Get-Content .squad\decisions.md

# Routing — does it reflect the actual team?
Get-Content .squad\routing.md

# Team — who's on it?
Get-Content .squad\team.md

# Skills — did anything get captured?
Get-ChildItem -Recurse .copilot\skills\

# Agent histories — did they learn?
Get-ChildItem .squad\agents\ -Recurse -Include history.md | ForEach-Object {
    Write-Host "`n=== $($_.FullName) ===" -ForegroundColor Yellow
    Get-Content $_
}

# Identity — current focus and wisdom
Get-Content .squad\identity\now.md
Get-Content .squad\identity\wisdom.md
```

**The honest test:** for each file, ask "would I write this if I were taking notes for myself?" If yes, the team is doing real work. If the files read like AI-generated meeting minutes nobody will ever consult again, you learned something equally valuable.

### What "good" looks like

- `decisions.md` has decisions that reference each other across step 5 and step 7 — i.e., the second-feature work *cited* the storage decision from earlier.
- `agents/<name>/history.md` contains specific, useful project facts (file paths, conventions, why things are the way they are) — not "I am the Backend agent and I write backend code."
- `wisdom.md` has at least one pattern that would survive being applied to a different project.
- `routing.md` matches the actual cast and not the placeholder template.

### What "AI confetti" looks like

- Decisions are bullet lists of generic best practices with no project-specific rationale.
- Agent histories say variations of "completed the task successfully."
- Wisdom is platitudes ("write clean code, test edge cases").
- Skills folders contain auto-generated boilerplate nobody referenced during the work.

Either outcome is data. The point of inspecting is to know which one you're getting *before* you decide whether to use Squad on real work.

---

## You can stop here

Modules 1 and 2 together are a complete, honest evaluation of Squad's core value proposition: a repo-native team that builds, decides, and remembers. If the artifacts in step 9 are useful, you have your answer.

If you want to see the riskier corners — observability with .NET Aspire, and Squad's autonomous mode (Ralph) — continue to **[Module 3 — Advanced](03-advanced.md)**. That module is more "guided tour of optional capabilities" than "build something." Treat it accordingly.
