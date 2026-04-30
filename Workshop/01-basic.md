# Module 1 — Basic: Build a Tiny Full-Stack App with Your Repo-Native AI Team

> A solo-dev workshop: build something with Squad, not just opinions.

← Back to [Workshop Index](README.md)

---

## Goal

In one sitting, build a small but real app — a **Personal Reading List** — with:

- A .NET 9 minimal API backend
- A tiny React frontend
- Tests
- At least one architectural decision recorded by the team

The point is not to make the next unicorn. The point is to see whether Squad's team model genuinely reduces context friction.

---

## Prerequisites

Before starting, verify every tool is installed and at the required version. Run each command and compare your output.

### 1. Node.js 22.5.0 or later

Squad requires Node.js 22.5.0+ for the built-in `node:sqlite` module.

```powershell
node --version
```

**Expected:** `v22.x.x` (22.5.0 or later)

**If not installed or too old:**

```powershell
winget install OpenJS.NodeJS.22 --accept-source-agreements --accept-package-agreements
```

> After installing, **restart your terminal** so the new `node` is on your PATH.

### 2. .NET 9 SDK or later

```powershell
dotnet --version
```

**Expected:** `9.x.x` or later (e.g. `10.0.102`)

**If not installed:**

```powershell
winget install Microsoft.DotNet.SDK.9 --accept-source-agreements --accept-package-agreements
```

### 3. Git

```powershell
git --version
```

**Expected:** Any recent version (e.g. `git version 2.x`)

### 4. GitHub CLI (`gh`) — latest version

```powershell
gh version
```

**Expected:** `2.89.0` or later

**If not installed or outdated:**

```powershell
# Install
winget install GitHub.cli --accept-source-agreements --accept-package-agreements

# Or upgrade
winget upgrade GitHub.cli --accept-source-agreements --accept-package-agreements
```

### 5. GitHub CLI authentication

```powershell
gh auth status
```

**Expected:** `✓ Logged in to github.com account <your-username>`

**If not authenticated:**

```powershell
gh auth login
```

Follow the browser-based flow. Select `GitHub.com`, `HTTPS`, and authenticate.

### 6. GitHub Copilot CLI (standalone)

This is the primary interface for the workshop — **not** VS Code Chat.

```powershell
copilot --version
```

**Expected:** `GitHub Copilot CLI 1.0.24` or later

**If not installed:**

```powershell
winget install GitHub.Copilot --accept-source-agreements --accept-package-agreements
```

> This also installs PowerShell 7+ as a dependency if you don't have it.
> After installing, **restart your terminal** to pick up the new `copilot` command.

### 7. Squad CLI — latest version

```powershell
squad --version
```

**Expected:** `0.9.4` or later

**If not installed:**

```powershell
npm install -g @bradygaster/squad-cli
```

**If outdated:**

```powershell
npm install -g @bradygaster/squad-cli@latest
```

> Earlier versions (0.9.1 and below) had two rough edges in `squad doctor` — a missing `casting/registry.json` and noisy false-positive warnings. Both are fixed in 0.9.4, so just upgrade and move on.

### 8. PowerShell execution policy (Windows only)

If you see `UnauthorizedAccess` errors when running `squad`, fix the execution policy:

```powershell
Get-ExecutionPolicy -Scope CurrentUser
```

**Expected:** `RemoteSigned` or `Unrestricted`

**If it shows `Restricted` or `AllSigned`:**

```powershell
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned -Force
```

### Quick verification script

Run this all-in-one check:

```powershell
Write-Host "=== Squad Workshop Prerequisites ===" -ForegroundColor Cyan
Write-Host "Node.js:      $(node --version)"
Write-Host "npm:           $(npm --version)"
Write-Host ".NET SDK:      $(dotnet --version)"
Write-Host "Git:           $(git --version)"
Write-Host "GitHub CLI:    $(gh version | Select-Object -First 1)"
Write-Host "Copilot CLI:   $((copilot --version 2>&1) -join '')"
Write-Host "Squad CLI:     $(squad --version)"
Write-Host "GH auth:       $((gh auth status 2>&1) | Select-String 'Logged in')"
Write-Host "PS Exec Policy: $(Get-ExecutionPolicy -Scope CurrentUser)"
Write-Host "===================================" -ForegroundColor Cyan
```

All lines should show valid versions + `Logged in` + `RemoteSigned`. If any fail, fix them before proceeding.

---

## Step 0: Create the repo and initialize Squad

### 0a. Create a fresh project directory

```powershell
mkdir reading-list-squad-lab
cd reading-list-squad-lab
git init
```

### 0b. Create a GitHub repo and set origin

```powershell
gh repo create reading-list-squad-lab --public --source . --push
```

> This creates the GitHub repo and links it as `origin` in one command. If the repo already exists, use:
> ```powershell
> git remote add origin https://github.com/<your-username>/reading-list-squad-lab.git
> ```

### 0c. Create a minimal README and push

```powershell
@"
# Reading List Squad Lab

A personal reading list app built as a Squad workshop exercise.

- .NET 9 minimal API backend
- React + TypeScript frontend
- SQLite or in-memory storage
"@ | Set-Content README.md

git add README.md
git commit -m "initial commit: project description"
git push -u origin master
```

### 0d. Initialize Squad

```powershell
squad init
```

**Expected output:** A list of created files under `.squad/`, `.github/`, and `.copilot/`, ending with `Your team is ready. Run squad to start.`

### 0e. Commit the Squad scaffolding

```powershell
git add -A
git commit -m "squad init: scaffold team workspace"
git push
```

### 0f. Verify everything is healthy

```powershell
squad doctor
```

**Expected (v0.9.4):** `9 passed, 0 failed, 0 warnings, 2 info` — no manual fixes needed.

#### Expected output (v0.9.4)

```
🩺 Squad Doctor
===============

Mode: local

✅  .squad/ directory exists — directory present
✅  config.json valid — parses as JSON, schema OK
✅  team.md found with ## Members header — file present, header found
✅  routing.md found — file present
✅  agents/ directory exists — directory present (2 agents)
✅  casting/registry.json exists — file present, valid JSON
✅  decisions.md exists — file present
✅  .github/agents/squad.agent.md — file present (Copilot agent discovery file)
✅  Node.js ≥22.5.0 (node:sqlite) — vXX.XX.X — node:sqlite available
ℹ️  vscode-jsonrpc exports field — not found in node_modules (expected for global installs)
ℹ️  copilot-sdk session.js ESM patch — not found in node_modules (expected for global installs)

Summary: 9 passed, 0 failed, 0 warnings, 2 info
```

The two `ℹ️` info lines are not warnings — Squad explicitly tells you they're expected when the CLI is installed globally (which it is, in this workshop). Both packages are bundled inside the Squad CLI itself, not in your project's `node_modules/`. No action needed.

> **If you're stuck on v0.9.1:** `squad init` won't scaffold `casting/registry.json` and `squad doctor` will report two warnings instead of info. Either upgrade (`npm install -g @bradygaster/squad-cli@latest`) or check the [CHANGELOG](https://github.com/bradygaster/squad/blob/main/CHANGELOG.md) for your version.

---

## Step 1: Launch Copilot CLI with the Squad agent

From inside the `reading-list-squad-lab` directory, start the Copilot CLI:

```powershell
copilot --agent squad
```

You should see the Copilot CLI banner with `GitHub Copilot v1.0.24`, a connected VS Code notification, and a prompt ready for input.

> **Why Copilot CLI and not the VS Code Chat panel?**
> The Squad README says it directly: *"The interactive shell (squad with no arguments) has been deprecated. For the best Squad experience, use the GitHub Copilot CLI instead."*
> Copilot CLI gives you tool execution, agent routing, MCP access, and full Squad integration from the terminal.

### Enabling auto-approve (optional but recommended)

Squad makes many tool calls per session. To avoid approving each one:

```
/allow-all
```

This is the equivalent of the `--yolo` flag. You can also start with:

```powershell
copilot --agent squad --yolo
```

### Selecting the right model

By default, Copilot CLI may start with a mid-tier model (e.g. `GPT-5.4 (medium)`). For a workshop that involves multi-agent coordination, code generation across .NET and React, architectural decisions, and code review — **use the strongest model available.**

Check the current model and available options:

```
/model
```

**Recommended models (in order of preference for this workshop):**

| Model | Why |
|---|---|
| **Claude Sonnet 4** or **Claude Opus** | Excellent at multi-step coding, nuanced review, and architectural reasoning |
| **GPT-5.4 (large)** | Stronger reasoning than medium; good all-rounder |
| **o3** or **o4-mini** | Strong reasoning models for complex planning |
| GPT-5.4 (medium) | Acceptable default, but may produce shallower reviews and weaker coordination |

Select the model you want from the list shown by `/model`. The model affects every agent in the session — Lead review quality, Tester edge-case detection, and Scribe memory all benefit from a more capable model.

> **Tip:** If you're unsure what's available, just run `/model` and pick the largest/most capable option. You can always switch mid-session.

---

## Step 2: Start with a lean solo-dev team

At the Copilot CLI prompt (`❯`), type:

```
I'm a solo developer building a small full-stack app called "Reading List."
Use a lean team: Lead, Backend, Frontend, Tester, and Scribe.
Stack: .NET 9 minimal API, React with TypeScript, and simple SQLite storage.
Keep architecture boring and maintainable.
Set up the team now.
```

**What to watch for:**
- Squad should propose team members, each with a name from a thematic cast
- Each member gets a role (Lead, Backend, Frontend, Tester, Scribe)
- You will be asked to confirm — type **yes**

### PRD / spec prompt

Squad may ask whether you have a PRD or spec document:

```
Do you have a PRD or spec document? (file path, paste it, or skip)
❯ 1. Skip — I'll give tasks directly
  2. Yes, let me provide one
  3. Other (type your answer)
```

**Select `1. Skip — I'll give tasks directly`.** In this workshop, we feed the team structured task prompts step by step instead of a single upfront document. Step 3 is our "spec" — giving the team the stack, folder structure, and architecture plan as a direct prompt.

**After confirmation, verify:**

```powershell
# In a separate terminal (not the copilot session):
dir .squad\agents\
```

You should see directories for each team member (e.g. `ralph/`, `scribe/`, and your newly cast agents).

---

## Step 3: Make the team explore first

Back in the Copilot CLI session, type:

```
Before building anything, explore the repo, propose the folder structure,
capture one architecture decision in decisions.md, and explain the
implementation plan. Don't write any code yet — just plan.
```

**What to watch for:**
- The Lead should propose a folder structure (e.g. `backend/`, `frontend/`, `tests/`)
- An architecture decision should be captured
- The Scribe should log the planning session and merge decisions

### How decisions flow in Squad

Squad uses a staging workflow for decisions:

1. **An agent writes a decision** to `.squad/decisions/inbox/<agent>-<topic>.md`
2. **The Scribe picks it up**, merges it into `.squad/decisions.md`, and clears the inbox file
3. The inbox ends up empty — this is normal

So after this step, the inbox will be empty:

```powershell
# This will be empty — that's expected:
Get-ChildItem .squad\decisions\inbox\
```

The merged decision lives in `.squad/decisions.md`:

```powershell
# This is where decisions end up:
Get-Content .squad\decisions.md
```

You should see a structured decision entry with rationale and alternatives considered — not just chat fluff.

> **Note:** The Scribe may also place a copy at the repo root (`decisions.md`). The authoritative file is always `.squad/decisions.md`.

---

## Step 4: Build the first vertical slice

Now give one bounded feature:

```
Build the first vertical slice:
- Backend: .NET 9 minimal API endpoint to add a book (POST /api/books)
- Backend: .NET 9 minimal API endpoint to list all books (GET /api/books)
- Frontend: React page that displays the book list
- Frontend: Simple form to add a book (title, author, status: unread/read)
- Tests: Unit tests for the API endpoints
Keep it minimal but production-clean. Use the folder structure from the plan.
```

**What to watch for:**
- Role separation: Backend agent writes API code, Frontend agent writes React code, Tester writes tests
- The Lead should coordinate and review
- The Scribe should log what happened

**After it completes, verify the code was created:**

```powershell
# In a separate terminal:
Get-ChildItem -Recurse -Name -Include *.cs,*.tsx,*.ts,*.json | Where-Object { $_ -notmatch 'node_modules|\.squad' }
```

You should see backend `.cs` files, frontend `.tsx`/`.ts` files, and test files.

**Try building and running:**

```powershell
# Backend
cd backend   # or wherever the API was placed
dotnet build
dotnet test

# Frontend
cd ../frontend  # or wherever the React app was placed
npm install
npm run build
```

> If paths differ from the above, check what the team actually created. The agents decide the exact structure.

---

## Step 5: Force an architectural decision

Ask the Lead directly:

```
Lead, decide whether we should use SQLite with Entity Framework or a simple
JSON file for storage in this workshop. Record the decision with rationale
in decisions.md. Consider: this is a workshop app, we want minimal setup
but realistic patterns.
```

**What to watch for:**
- A deliberate decision, not just "use SQLite because it's popular"
- The decision should appear in `.squad/decisions.md` with context and rationale
- Other agents should be able to reference this decision in future work

**Verify:**

```powershell
Get-Content .squad\decisions.md
```

Look for a new entry with a clear rationale section.

---

## Step 6: Use the reviewer on purpose

Now trigger the review cycle:

```
Lead, review all changes so far as if this were a real PR. Be specific
about what's good and what needs improvement.

Tester, look for edge cases we skipped — empty titles, duplicate books,
invalid status values. Add tests for them.

Scribe, capture any new skills or patterns we should preserve for future work.
```

**What to watch for:**
- The Lead should give concrete review feedback (not just "looks good")
- The Tester should find and write tests for edge cases you didn't think of
- The Scribe should update skills in `.copilot/skills/` or `.squad/identity/wisdom.md`

**Verify:**

```powershell
# Check for new test files or updated tests
Get-ChildItem -Recurse -Name -Include *test*,*Test*,*spec* | Where-Object { $_ -notmatch 'node_modules' }

# Check for skills
Get-ChildItem -Recurse .copilot\skills\

# Check wisdom
Get-Content .squad\identity\wisdom.md
```

This is where Squad is supposed to earn its keep. The solo-dev docs position the Lead as the safety net and the Tester as the discipline you would otherwise skip when tired, hungry, or overconfident. Usually all three.

---

## What to watch for

Success is not "the app compiles." A determined toaster can probably do that soon.

**Success looks like:**

- [ ] The Lead catches something useful during review
- [ ] The Tester adds test cases you would have skipped
- [ ] Decisions are actually preserved in `decisions.md` and referenced later
- [ ] The team structure reduces re-explaining context

**Failure looks like:**

- [ ] Too many agents for the task (overhead > value)
- [ ] Decorative memory (files exist but are never reused)
- [ ] Vague work splitting (agents duplicate effort or produce incoherent output)
- [ ] Endless agent theater with no measurable improvement over a single Copilot session

That is why this is a workshop and not a demo. Demos flatter tools. Workshops embarrass them. Which is healthy.

---

## You can stop here

If module 1 was useful, you have a working app, a working team, and enough exposure to decide whether Squad is worth keeping in your toolbelt.

If you want to test whether the persistent memory actually compounds — i.e., whether the *second* feature goes faster than the first — continue to **[Module 2 — Intermediate](02-intermediate.md)**.
