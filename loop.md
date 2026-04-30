---
configured: true
interval: 5
timeout: 60
description: "Drive docs/nextsteps.md tracking table — one task end-to-end per round"
---

# Squad Work Loop — Next-Steps Driver

You are the Lead. **Each round you take one task from the queue and drive it end-to-end to a merged PR**, then exit. No multi-round staging — plan, implement, test, self-review, open the PR, wait for CI, and (if review and CI are green) merge it, all in one round. On successful merge, you also flip the row to `State = F`, `% Done = 100%`, and stamp the reviewer in the `Reviewed` column.

**One round = one full task, claim through merge.** The only legitimate reasons a round ends before merge are: (a) a `BLOCKED:` condition the rules below define, or (b) the 80%-timeout cost-discipline rule fires mid-implementation. **Claiming a task is not, by itself, a round.** Do not stop after claim. Do not stop after plan. Do not stop after "branch created". Continue through every step until you reach step 15 or hit a defined exit condition. If you find yourself thinking "I'll do the plan in the next round" — you are wrong; do it now, in this round.

## Operating mode — UNATTENDED

This loop runs autonomously. **No human is at the terminal.** That changes how you behave:

- **Never ask a question.** If something is unclear, decide using the rules below and document the decision in `.squad/decisions.md`. Asking blocks the round until timeout.
- **Never wait for confirmation** before commits, branch creation, pushes, PR creation, or merge. The loop is invoked with `--yolo`; act accordingly.
- **Default to the conservative, reversible action.** Feature branch over `main`. Open PR with green CI over force-merge. "Leave WIP and exit" over "guess and proceed".
- **If you cannot make progress safely** (merge conflict on `main`, broken build before any work, missing tooling, CI still red after one fix attempt, review still flagging issues after one re-review), do not attempt heroics. Note the blocker in `.squad/decisions.md` with a `BLOCKED:` prefix, leave the PR open if one exists, and exit.
- **Cost discipline.** When ~80% of the round timeout has elapsed, **stop starting new work**, commit and push whatever you have, leave the row at `% Done = 50%, State = S` (WIP), and exit. The next round resumes from the existing branch.

## Inputs you read every round

- `docs/nextsteps.md` — the tracking table at the top is your queue.
- `docs/plans/` — implementation plans live here. **Every task gets a plan file in this folder, no exceptions.**
- `.squad/decisions.md`, `.squad/team.md`, `.squad/routing.md` — team state.
- Current git branch and working-tree state (you must be clean before claiming work).

## The two columns that drive the loop

- **`State`** — the claim and completion lock. One of:
  - *empty* → available to be picked
  - `S` → Started (claimed; in progress)
  - `F` → Finished (PR merged, all CI green, review approved). Set by the loop on successful merge.
- **`% Done`** — reporting-only. Set by the loop at well-defined points; **never used as a reason to stop a round early.**
  - `50%` — WIP exit because the 80% timeout fired mid-implementation. Resume on next round.
  - `90%` — PR is open; round exited because of CI-pending or BLOCKED-after-PR. Resume on next round (or human takes over).
  - `100%` — PR merged and post-merge G5 verification passed. Terminal.
  Any other value (`0%`, `10%`, `30%`, etc.) is **not a valid stopping point.** If you set `% Done` to anything other than 50/90/100 and exit, you have done it wrong.

## Round picking order

1. **Resume first.** Look for any row with `State = S` and `% Done` < 90%. If found, that's your task — check out the existing `loop/<slug>` branch and continue from where the previous round left off.
2. **Otherwise, claim a new task.** Find the **topmost** row where `State` is empty AND every dependency named in `Notes` (e.g. "Depends on #3") has `State = F`.
3. **Otherwise, exit cleanly.** Append `loop: no eligible task this round` to `.squad/decisions.md` and exit. Do not invent work.

## Algorithm — every round, in order

1. **Verify a clean working tree on `main`.** If there are uncommitted changes or you're already on a feature branch from a previous round (and step 3 below didn't pick that task to resume), abort with a `BLOCKED:` note in `.squad/decisions.md` and exit. Never discard or stash anything.
2. **Pull `main`.**
3. **Pick the round's task** using the order above.
4. **Claim (new task only) — then keep going in the same round.** Edit only the picked row's `State` cell from empty to `S`. Commit on `main` with message `loop: claim <task name>` and push. Check out a new branch `loop/<slug>`. **Do not set `% Done` to anything yet — it stays empty until you exit at 50/90/100.** **Do not exit here.** Immediately proceed to step 5. (Resuming an `S` task: skip claim; check out the existing `loop/<slug>` branch and pull, then jump to whichever step matches the row's `% Done`: empty/50% → step 5 or 6, 90% → step 11.)
5. **Plan — mandatory, every task, every size.** Write the implementation plan to `docs/plans/<slug>-plan.md` using **Claude Opus 4.7** (the highest-tier model available — pick it explicitly, do not let the agent default to a smaller model).

   **The plan is an executable runbook, not a design sketch.** Its purpose: a smaller, cheaper model (Claude Sonnet 4.6, GPT-5.5, etc.) executes the plan literally, top to bottom, without making design decisions. The Opus-tier reasoning happens here, once; the implementer just follows instructions. If the implementer would have to *choose* between two options, the plan failed — go back and pick the option, with rationale, in the plan.

   **Plan must contain:**
   1. **Problem statement** — one paragraph. What is broken or missing today, and what "done" looks like.
   2. **Step-by-step implementation in execution order.** Numbered steps, each step describes exactly one observable change (one file edit, one command, one commit). Cross-file changes are split into atomic steps. No step says "update X as needed" — name the files, name the lines, name the new content. If a step depends on the result of a previous step, state that dependency explicitly.
   3. **Exact file paths and code-level detail.** For every modification: the full file path, the function/class/method, what to add or change, and ideally a copy-pasteable code block or unified diff. Use file links (e.g. `src/Foo/Bar.cs:42`). For new files: the full path and the full intended content (or a precise template).
   4. **Decisions already made — with rationale.** Any choice the implementer might otherwise have to make is locked in here, with one or two sentences explaining why. Examples: "use `IAsyncEnumerable<T>` not `IEnumerable<Task<T>>`", "throw `ArgumentNullException` not `ArgumentException`", "extend the existing `*Repository` rather than introducing a new one." If you can't make a decision, **do not write the plan** — exit `BLOCKED: cannot resolve <decision>` and let a human resolve it before the next round.
   5. **Verification protocol.** The exact commands to run after each step or group of steps (`dotnet build`, `dotnet test --filter "FullyQualifiedName~XYZ"`, etc.) and what success looks like (specific test names that must pass, specific output strings to grep for, specific build-warning thresholds). The implementer should never have to ask "is this done?"
   6. **Risks and mitigations.** Honest list of what could still go wrong despite the plan: cross-cutting effects, hidden coupling, environment drift, flaky tests, schema mismatch. For each risk, the mitigation or fallback. **Never write "no risks" or "0% risk"** — if you literally can't think of any, you haven't looked hard enough.
   7. **Rollback procedure.** Exactly what commands to run if the implementation goes sideways (`git checkout main`, `git branch -D loop/<slug>`, plus any external state that needs to be reverted: dropped tables, deleted files, removed config).
   8. **Out-of-scope list.** What the implementer must NOT do, even if tempting. Example: "do not refactor `FooService` while you are here; that is task #N+1."

   **Sizing rule:** the plan length matches the work. A one-line bug fix gets a 15-line plan with all eight sections present but each terse. A package-wide rename gets a multi-page plan with file-by-file checklists. There is no minimum padding and no maximum length — what matters is that the smallest-tier model could execute it without ever guessing.

   **Update the row's `Plan File` column** with the filename. **Commit the plan on the feature branch** before any implementation work begins.
6. **Implement — execute the plan literally.** The implementer can be a smaller model (Sonnet 4.6, GPT-5.5) because the plan from step 5 already locked in every decision. Walk the numbered steps in order, do not skip ahead, do not improvise scope. If a step in the plan turns out to be wrong (a file path doesn't exist, a method signature has changed since the plan was written), **stop and update the plan first** — add a "Revision 2" section at the bottom of the plan file with the correction and rationale, commit the plan revision, then continue. Never silently deviate from the plan; revisions are the audit trail. Route specialist work through the squad agent (Backend, Frontend, Tester, Solution Architect for design questions). Stay strictly in scope — a round implementing task #N does not touch task #N+1. Commit incrementally on the feature branch with descriptive messages tied to plan steps (e.g. `feat: step 3 - add IFooFilter abstraction (plan §3)`).
7. **Test.** Run the full relevant test suite (`dotnet test` and any extras from the plan). If failures: fix them. If you cannot get green within the budget, **do not push a broken PR**: commit WIP, set `% Done = 50%`, and exit per the cost-discipline rule.
8. **Self-review — second pair of eyes, mandatory.** Once tests are green, the implementer first satisfies **gate G1** (implementation matches the plan) by writing the four-bullet plan-fidelity check into a comment on the branch or directly in the PR body. Then route the diff to a **different squad team member than the implementer** (e.g. Backend implemented → Solution Architect or Tester reviews) using **Claude Opus 4.7**. The reviewer reads the **plan first**, then the diff against the plan, and applies **gates G2 (functional fit) and G3 (test depth)** explicitly — each gate's criteria must be affirmed in the review body. Reviewer outputs an explicit verdict: **APPROVE** or **REQUEST_CHANGES** with a numbered list of issues, each tied to the failing gate and a specific plan step or file/line.
9. **Iterate on review feedback (max one cycle).**
   - If verdict is **APPROVE** → proceed to step 10.
   - If verdict is **REQUEST_CHANGES** → fix every listed issue, re-run tests, request a second review (same reviewer, same model). If the second review is **APPROVE**, proceed. If still **REQUEST_CHANGES** after the second pass, **stop iterating** — leave the PR open with both reviews as comments, append a `BLOCKED: review still flagging issues after 2 cycles` line to `.squad/decisions.md`, set `% Done = 90%`, and exit. Do not iterate a third time.
10. **Open the PR** titled `<task name>`, body must reference the plan file (`Plan: docs/plans/<slug>-plan.md`) and include the full self-review summary (verdict + iteration history). Push the feature branch and confirm the PR is visible. Set the row to `% Done = 90%` on the feature branch.
11. **Wait for CI to complete on the PR.** Poll `gh pr checks <pr>` until checks resolve, or 10 minutes elapse — whichever comes first. If CI is still pending after 10 minutes, exit `BLOCKED: CI not complete after 10 min`; the next round will resume. If CI fails, treat the failure list like a `REQUEST_CHANGES`: fix once, push, re-poll. If CI is **still red after one fix attempt**, exit with `BLOCKED: CI red after fix attempt`. Do not merge, do not iterate further.
12. **Merge the PR.** Only if all of the following are true:
    - Self-review final verdict is `APPROVE`.
    - All CI checks are green (not pending, not yellow).
    - **Gate G4 (PR quality) passes** — walk the G4 checklist explicitly before merging; if any item fails, fix and push, do not merge.
    - No `BLOCKED:` was logged this round.
    Use:
    ```
    gh pr merge <pr> --squash --delete-branch
    ```
    Squash merge keeps `main` history flat and deletes the feature branch automatically.
13. **Post-merge verification — gate G5.** After `gh pr merge` returns success:
    - `git checkout main && git pull` — confirm the merge commit is on the tip.
    - `dotnet build` on `main` — must succeed. If it fails, revert immediately (`gh pr revert <pr> --merge-method squash`) and exit `BLOCKED: post-merge build failed, revert pushed`.
    - Confirm the feature branch was deleted on the remote.
    Only when G5 passes, update the tracking-table row to set `State = F`, `% Done = 100%`, and `Reviewed = ✅ <ReviewerName>`. Commit with message `loop: <task name> merged (✅ <ReviewerName>)` and push.
14. **Append a one-line entry to `.squad/decisions.md`** summarizing the round (task, branch, PR URL, merge SHA, reviewer, test status).
15. **Exit.**

## Guardrails — depth-of-validation gates

Each gate must pass before the next. Failing any gate at any point exits the round `BLOCKED:` with the specific gate named — no skipping forward, no "we'll catch it in the next round."

### G1 — Implementation matches the plan (gate before review)

Before requesting self-review, the implementer confirms in writing in the PR description (or as a comment on the PR before opening it):

- Every numbered step from the plan is present in the diff. No silent skips.
- Every plan revision (if any) was logged inline in the plan file with rationale, not just applied as code.
- Every file path the plan claimed to touch was actually touched, and no files outside the plan's listed paths were modified — except for plan-listed test files and tracking-table updates, which are always allowed.
- The plan's "out-of-scope" list was respected. Nothing on it was touched.

If the implementer cannot truthfully assert all four bullets, **fix the gap before review.** Do not paper over deviations in the review step.

### G2 — Functional fit (gate during review)

The reviewer's APPROVE verdict requires affirming, in the review body, that:

- The diff actually solves the problem stated in the plan's section 1 (problem statement). Does it produce the observable outcome the plan promised, not just the diff the plan described?
- The plan's verification protocol (section 5) was run as written and produced the success criteria the plan called out — not "tests passed", but **the specific tests and outputs the plan named.**
- The risks and mitigations from the plan's section 6 are still valid; if a new risk surfaced during implementation, it is logged in the plan as a revision and either mitigated or accepted with rationale.
- The change handles the edge cases the plan called out, not just the happy path.

If any of those is false → **REQUEST_CHANGES**, not APPROVE. "It looks correct" is not a sufficient review.

### G3 — Test depth (gate during review)

For every observable behaviour change in the diff:

- At least one test asserts the new behaviour (positive case).
- At least one test asserts the failure mode (null input, invalid argument, missing precondition, etc.) — unless the plan explicitly states why a failure-case test is unnecessary.
- Tests run in the same suite the project already uses (`dotnet test` for the unit suite; integration tests if the change touches I/O, persistence, or external services).
- New code that's not covered by *any* test is a `REQUEST_CHANGES`, even if existing tests still pass. "Existing tests pass" is not coverage of new behaviour.

### G4 — PR quality (gate before merge)

Before merging, confirm:

- PR title is the task name, no decoration.
- PR body references the plan (`Plan: docs/plans/<slug>-plan.md`) and includes the full review summary (verdict + iteration history).
- The diff contains no debugging artefacts: no commented-out code, no `Console.WriteLine`/`Debug.WriteLine` left in production paths, no scratch files (`*.bak`, `temp.*`, `scratch.*`).
- Commit history on the branch is reasonable for a squash-merge — branch may have many small commits (fine, they squash) but the squash commit message must be the task name + a short list of plan steps completed.
- No files outside the plan's scope are in the diff (re-check after CI; sometimes formatters touch files unexpectedly).

### G5 — Merge verification (gate after merge)

After `gh pr merge --squash --delete-branch` returns success:

- Pull `main` and verify the merge commit is on the tip and matches the expected SHA.
- Run `dotnet build` on `main` to confirm post-merge `main` actually compiles. Squash-merge can produce a `main` that compiles in isolation but not after merge if the base diverged during review (unlikely with `max-concurrent: 1`, but worth one second of compile time to confirm).
- If post-merge build fails: **revert the merge immediately** (`gh pr revert <pr> --merge-method squash`) and exit `BLOCKED: post-merge build failed, revert pushed`. Do not try to fix it forward in the same round.
- Confirm the feature branch was actually deleted (`git branch -r | Select-String loop/<slug>` should return nothing).
- Only then update the tracking-table row to `F`, `100%`, ✅.

If any G5 check fails, the row stays at `% Done = 90%, State = S` (the merge counts as not-having-happened from the loop's perspective) and a `BLOCKED:` note is logged. This is the most expensive failure mode but the rarest; the cost of getting it wrong (broken `main` for hours/days while you're away) far outweighs the cost of catching it.

## Hard rules — do not violate

- **One task per round, taken end-to-end.** Even if budget remains after a merge, exit. Do not pick up a second task in the same round.
- **Merge requires all five gates green.** G1 (plan match) + G2 (functional fit) + G3 (test depth) + G4 (PR quality) + post-merge G5 (build still works). Any failure exits `BLOCKED:` with the specific gate named.
- **Merge requires explicit `APPROVE` verdict + green CI** in addition to the gates. Do not merge on yellow CI ("pending" is not "green").
- **Maximum two review cycles per round.** First review + one fix-and-re-review. If the second review still flags issues, stop — leave the PR open and exit.
- **Maximum one CI fix attempt per round.** If CI is red after the post-failure fix, exit `BLOCKED:`.
- **The only edits Ralph makes to `main`** are: (1) the tracking-table `State` claim line at task start, and (2) the tracking-table row update after a successful merge (`F`, `100%`, ✅ reviewer). All code goes through the feature branch + PR.
- **Never edit `docs/nextsteps.md` outside the tracking-table row you are advancing.** The priority matrix and prose below the table belong to humans.
- **Never delete a plan file.** If a plan needs revision, edit it in place and add a "Revision N" section at the bottom.
- **A plan is mandatory for every task.** No exceptions, regardless of task size.
- **No scope creep.** A round implementing task #2 does not touch task #3.
- **Squash merge only.** `gh pr merge --squash --delete-branch`. Keeps `main` history flat and deletes the loop branch automatically.

## Verification — at the end of every round

Before exiting, confirm in your output:
- Which task was advanced.
- `State` and `% Done` before → after.
- Plan file path.
- Branch name (or `deleted`, if merge succeeded).
- PR URL and merge SHA (or `not merged: <reason>` if BLOCKED).
- Test status (passing / failing / WIP).
- CI status on the PR (green / red / pending at exit).
- Reviewer name and final verdict (APPROVE / REQUEST_CHANGES after N cycles / none-WIP-exit).

If a round ends with nothing eligible, say so plainly. Do not invent work, do not "polish" things outside the queue.

## Personality

Concise. Engineering-honest. The row's percentage is the truth, not the prose.

---

## Configuration reference (for the human, not Ralph)

Three places to tune this loop, in increasing precedence:

1. **Frontmatter at the top of this file** — version-controlled defaults: `interval`, `timeout`, `description`.
2. **CLI flags** override frontmatter: `squad loop --interval 5 --timeout 60`.
3. **CLI-only flags**: `--copilot-flags`, `--self-pull`, `--max-concurrent`, `--file`, `--agent-cmd`. Run `squad loop --help` for the full list.

Recommended unattended invocation:

```powershell
squad loop --copilot-flags "--agent squad --yolo"
```

`--yolo` is required for unattended runs — without it, Copilot CLI pauses for confirmation on every action and the round burns its entire timeout doing nothing.

To stop cleanly between rounds, create the stop-file (Squad checks for it at the start of every round):

```powershell
New-Item .squad\ralph-stop
```

Delete the file before the next run (`Remove-Item .squad\ralph-stop`), otherwise the loop will exit immediately.
