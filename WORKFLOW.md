# Workflow: TODO status, subagents, reviews, commits

For the main agent (the one talking to the user) and the review orchestrator. **Implementer and reviewer subagents don't need this file**: they read `CLAUDE.md` (environment, tools, pitfalls, game knowledge) plus the mod's docs.

Section numbers are shared with [`CLAUDE.md`](CLAUDE.md) (0, 1, 2b and 2c are here; 2, 2a and 3–8 are there), so a reference such as "section 2b" is unambiguous.

## 0. Where information lives

Each fact lives in one file. Look there first, and update it there.

| File | What it holds | Written by |
|---|---|---|
| [`TODO.md`](TODO.md) | The user's requests, each with a **Status** block (state, code location, review record, what it waits on). The only place that tracks status. | The user writes the requests; agents maintain only the Status blocks |
| [`README.md`](README.md) | Project overview, mods table, install and build, log collection | agents |
| [`CLAUDE.md`](CLAUDE.md) | How to work here: where changes go, environment, tools, interop pitfalls, and the game systems that several mods share. Read by every agent. | agents |
| `<Mod>/README.md` | What the mod does, for players and publishing: settings, who needs it in multiplayer, compatibility. **Required for every mod.** | agents |
| `<Mod>/CLAUDE.md` | What agents would otherwise have to re-derive about that mod: mechanism, patches, multiplayer reasoning, pitfalls, test steps, and game knowledge only that mod uses. **Optional.** | agents |
| Doc comment at the top of each patch file | The full root-cause analysis | agents |
| [`REVIEW_ORCHESTRATOR.md`](REVIEW_ORCHESTRATOR.md) | An agent definition for the implement-and-review loop. Load it into a fresh session; it isn't general guidance. | the user |

Agents read a mod's `README.md` and `CLAUDE.md` before working on it (stated in the root `CLAUDE.md`).

Per-mod information never goes in a root file.

## 1. Always start with `TODO.md`

[`TODO.md`](TODO.md) is the source of truth for what to work on. The user updates it over time, so **read it fresh at the start of every session**; don't rely on memory or older notes.

Each entry is a heading (`## Bug #N: ...` or `## Improvement #N: ...`), a **Status** block, and the user's request. The block looks like this:

```
**Status:** reviewed
**Code:** `SupermarketFixes/src/Patches/GhostHeldItemFix.cs`
**Review:** 2026-11-15, 1 round, hash `d48d7436015a`
**Waiting on:** In-game test + logs.
```

- `Status` is always present. `Code` is the patch file (bugs) or the mod folder (improvements), once there is code. `Review` appears once a review passed. `Waiting on` is one short line; anything longer goes in the mod's `CLAUDE.md`.
- **Don't rewrite or reorder the user's request text, and don't add analysis to TODO.md.** Change only the Status block. Investigations and notes go in the mod's `CLAUDE.md` (for bugs, `SupermarketFixes/CLAUDE.md`).
- If an entry is a question rather than a code task (Bug #4 asks how to collect logs), answer the user directly. The README's log section covers the common case.

States, one per entry, in this lifecycle:

| State | Meaning | Set by |
|---|---|---|
| `open` | No code yet | (initial) |
| `in-progress` | Code is being written, or is only partly done. Continue from `Code`; don't start over. | the implementing agent |
| `implemented` | An implementer finished it and it builds, docs included. **Not yet code-reviewed**: the mandatory review (section 2b) is pending or running. Never the resting state when reporting to the user. | the implementing agent / main agent, right after the implementer reports |
| `reviewed` | **Code review only**: the mandatory review is finished and reviewer and implementer agreed. Pending the user's in-game test. | the review orchestrator, or the main agent after the review loop |
| `needs-rework` | The in-game test failed, or the user asked for changes | whoever records the user's feedback |
| `done YYYY-MM-DD` | **Tested in-game and accepted by the user** | only after the user confirms |

Allowed moves: `open` → `in-progress` → `implemented` → `reviewed` → `done`. **No skipping the review:** `implemented` never goes straight to `done`. Any state → `needs-rework` on failure or feedback; `needs-rework` → `in-progress`.

- **Skip `done` entries.** `implemented` and `reviewed` are never `done`: a build or a code review isn't an in-game test.
- **Stale reviews:** the `Review` line stores a hash of the reviewed code (`tools/modhash.sh <path in "Code">`). If the current hash differs, the code changed after the review: set the state back to `implemented`. A bug's hash covers only its patch file; shared files such as `Plugin.cs` aren't hashed.
- Update the Status block in the same change as the work it describes.

## 2b. Subagents do the work; every change is reviewed

**The main agent (the one talking to the user) never changes mod code itself.** Every code change, whether a bug fix, an improvement, a review-round fix or a small tweak, is done by an **implementer subagent** launched with the Agent tool and `model: "sonnet"`. The main agent only reads, plans, briefs, relays and reports; it may edit docs and `TODO.md` Status blocks directly. This is what makes the model setting below enforceable.

**Reviews are mandatory.** When the implementer has finished (it builds, and the mod's README, its CLAUDE.md if any, and the Status block in TODO.md are updated), set the entry to `implemented`, commit, and **launch a review subagent before you report back to the user**. Don't report an item as ready for the in-game test while it is still `implemented`; it must reach `reviewed` first. This applies to every code change, including review-round fixes (re-review until reviewer and implementer agree) and `needs-rework` changes. Doc-only and `TODO.md`-only changes need no review. (When working under `REVIEW_ORCHESTRATOR.md`, the orchestrator runs this loop instead.)

Launch the review subagent with the Agent tool:

- **Models are fixed:** implementer agents always run on `sonnet`, reviewer agents always on `opus`. Pass `model: "sonnet"` or `model: "opus"` explicitly on every Agent call; never rely on the default.
- **Brief it with the intent only.** Describe *what the change was meant to achieve*: the bug symptom or the feature, and the expected behaviour (including multiplayer expectations, if relevant). Name the mod or folder it lives in so the reviewer knows where to look, and tell it to read that mod's docs.
- **Don't include implementation details or your reasoning.** No root-cause theory, no explanation of why you chose this approach, no list of the methods you patched, no "I think this is fine because...". The reviewer must form its own opinion from the code, not inherit your assumptions.
- **Don't steer the verdict.** Don't say what to focus on or what to ignore, and don't hint that the change is safe or risky. Just ask for a review of whether the change correctly and safely achieves that intent, and for concrete findings ranked by severity.
- **Act on the result.** Read the review critically, then apply the changes that are justified (through an implementer). If you disagree with a finding, say so in your final report to the user with your reason; don't silently drop it. Rebuild after applying changes.
- When the review agrees, set `reviewed` with the `Review` line. In your final report, say that the review happened and what changed because of it, and give the in-game test steps.

## 2c. Commit often

The repo is a git repo; commit as you go so every step is recoverable and the history shows what changed and why.

- **Commit at each stable point:** a fix or improvement that builds, a review round applied, a docs or `TODO.md` update, a new tool. Don't batch unrelated work into one commit.
- **One logical change per commit.** Keep a mod's code and its README/CLAUDE.md update in the same commit; keep changes to different mods in separate commits.
- **Commit messages are a brief title only:** one short imperative line (for example `Fix rack move desync when guest joins`). No body, no trailers or attribution lines, and no type tags or prefixes such as `feat:`, `fix:` or `chore:`.
- **Stage explicitly** (`git add <paths>`), never `git add -A` blindly. Don't commit `bin/`, `obj/`, `decompiled/` (all gitignored) or log files such as `bug-1/`.
- Commit before starting a risky refactor and after the review loop (section 2b) applies its changes, so a bad change can be reverted cleanly.
- Don't push, amend or rewrite history unless the user asks.
