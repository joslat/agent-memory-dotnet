# Squad Workshop — Index

> A solo-dev workshop on Squad: build something with a repo-native AI team, see whether the team model genuinely reduces context friction, and learn where it earns its keep — and where it doesn't.

This workshop is split into three modules. Each module is self-contained and stops at a natural checkpoint, so you can stop after the basic module and still have a working app and a useful read on the tool.

---

## Modules

| # | Module | What you build | Time | Extra prerequisites |
|---|---|---|---|---|
| 1 | [Basic](01-basic.md) | A working .NET 9 + React reading list app, built by the team end-to-end with one architectural decision and a real review pass. | ~90 min | None beyond the prereqs in module 1. |
| 2 | [Intermediate](02-intermediate.md) | A second-wave feature on the same app — filtering, validation, regression-aware tests — to see whether persistent memory actually compounds. Inspect the team's artifacts. | ~45 min | Completed module 1. |
| 3 | [Advanced](03-advanced.md) | Observe Squad with .NET Aspire, then graduate to autonomous mode with **Ralph** — `squad triage`, watch mode, and prompt-driven loops. | ~60 min | Completed module 1 (module 2 is recommended). **Docker Desktop running** (for Aspire). |

> **Honest about scope:** modules 1 and 2 are the workshop. Module 3 is more of a guided tour of the riskier corners — autonomous execution, observability — and it deliberately doesn't ask you to leave Ralph running on your repo unsupervised.

---

## Module summaries

### [Module 1 — Basic](01-basic.md)

Get from zero to a real, working full-stack app driven by the team. Cover the essential muscle memory: scaffolding the team, planning before building, recording a decision, building a vertical slice, and using the reviewer on purpose.

Index:

- Goal & success rubric
- Prerequisites (8 tools + verification script)
- Step 0: Create the repo and initialize Squad
- Step 1: Launch Copilot CLI with the Squad agent
- Step 2: Start with a lean solo-dev team
- Step 3: Make the team explore first
- Step 4: Build the first vertical slice
- Step 5: Force an architectural decision
- Step 6: Use the reviewer on purpose
- What to watch for (success vs. failure)

You can stop here and have a complete answer for "should I keep using Squad?"

### [Module 2 — Intermediate](02-intermediate.md)

The compounding-memory test. Add a second feature on top of the work from module 1 and see whether the team gets faster because the repo remembers, or just retraces steps. Then inspect the artifacts the team left behind.

Index:

- Step 7: Add a second-wave feature
- Step 8: Commit and push (manual or via the team)
- Step 9: Look inside `.squad/` — decisions, routing, skills, agent histories, identity

If module 1 felt like "AI confetti," this is where you find out for sure.

### [Module 3 — Advanced](03-advanced.md)

Two separate, optional capabilities — both worth knowing about, neither needed for daily work.

Index:

- Step 10: Observe it with Aspire (requires Docker Desktop)
- Step 11: Try Ralph — Watch Mode (autonomous polling and execution)
- Step 12: Prompt-driven loops (`squad loop`)
- Honest tradeoffs and when to walk away

The Aspire section was rough on at least one machine in the wild — module 3 documents the failure modes and the fix.

---

## Quick reference: Squad commands

| Command | Purpose |
|---|---|
| `squad init` | Scaffold Squad in the current directory |
| `squad doctor` | Diagnose setup issues |
| `squad status` | Show active squad info |
| `squad upgrade` | Update Squad-owned files (never touches team state) |
| `squad upgrade --self` | Update the Squad CLI itself |
| `squad triage` | Watch mode — poll and triage issues |
| `squad triage --execute` | Watch mode with autonomous agent execution |
| `squad triage --health` | Show status of a running watch process |
| `squad loop` | Prompt-driven continuous work loop |
| `squad nap` | Context hygiene — compress, prune, archive |
| `squad aspire` | Open Aspire dashboard for observability (needs Docker) |
| `squad export` / `squad import` | Portable squad snapshots |

## Quick reference: Copilot CLI commands

| Command | Purpose |
|---|---|
| `copilot --agent squad` | Start Copilot CLI with the Squad agent |
| `copilot --agent squad --yolo` | Start with auto-approve for all tool calls |
| `/allow-all` | Enable all permissions inside a session |
| `/model` | Choose the model used for the session |
| `/quit` | Exit the Copilot CLI session |
| `/login` | Authenticate if not logged in |
| `/init` | Generate `copilot-instructions.md` |

---

## Returning to the project later

You don't run `squad init` again. From the project directory:

```powershell
squad status     # confirm which squad is active
squad doctor     # confirm setup is still healthy
copilot --agent squad
```

## Upgrading Squad

```powershell
npm install -g @bradygaster/squad-cli@latest    # 1. upgrade the CLI binary
cd <your-project>
squad upgrade                                    # 2. refresh Squad-owned files in this project
git diff                                         # 3. review what changed
git add -A; git commit -m "squad: upgrade templates"
squad doctor                                     # 4. verify
```

`squad upgrade` overwrites `squad.agent.md`, `.squad/templates/`, and the GitHub workflows. It **never touches** your team state — `team.md`, `routing.md`, `decisions.md`, agent charters and histories, `casting/registry.json`, identity files. Those are yours.

---

## Cleanup

When you're done with the workshop, you can delete the practice repo:

```powershell
gh repo delete reading-list-squad-lab --yes
cd ..
Remove-Item -Recurse -Force reading-list-squad-lab
```

---

## Environment used in this workshop

| Tool | Version |
|---|---|
| Node.js | 22.22.2 |
| .NET SDK | 10.0.102 |
| GitHub CLI | 2.89.0 |
| GitHub Copilot CLI | 1.0.24 |
| Squad CLI | 0.9.4 |
| PowerShell | 7+ |
| OS | Windows 11 |

If you're on a different OS, the substance is the same — only the package manager and shell quirks change.
