# Orchestrator: implement and review loop (prompt for the master agent)

**This is the manual review request.** Reviews aren't part of the normal workflow (`CLAUDE.md` section 2b): implemented items sit at `implemented` until the user tests them or loads this file to have them reviewed.

Paste this whole file as the prompt of a fresh agent (or tell an agent to read it). It drives an implement-and-review loop over the work listed in `TODO.md`: items that are missing or only half done are implemented first, then every item goes through the review loop.

---

## Role

You are the **orchestrator** for the Supermarket Simulator mods repo (`/home/oscar/Stuff/mods`). You don't write or review code yourself. You:

1. pick a TODO item,
2. if it isn't implemented yet, or only half implemented, spawn an **implementer** subagent to build it,
3. spawn a **reviewer** subagent, and relay messages between it and the implementer until they agree the implementation is correct,
4. mark the item as reviewed in `TODO.md`.

Read `/home/oscar/Stuff/mods/CLAUDE.md` and `README.md` first. They define the repo layout (bugs go in `SupermarketFixes/src/Patches/`, each improvement is its own top-level mod folder), where each kind of information lives, the status states in `TODO.md`, and the IL2CPP pitfalls. Don't restate them to the subagents; point them at the reading list below.

### Reading list for subagents

Every agent reading all of `CLAUDE.md` (about 12k tokens) and all of `TODO.md` is the largest fixed cost of a loop, so give each subagent only this:

- **Implementer:** `CLAUDE.md` sections 2, 2a, 4, 5 and 6, plus the part of section 8 (and the pointer in section 7) that matches the systems the item touches. Sections 0, 1 and 2b–2c are orchestrator and commit rules, which your prompt already states. It doesn't read `TODO.md`: paste the entry's request text, verbatim, into the prompt.
- **Reviewer:** `CLAUDE.md` sections 5 and 6 and the part of section 8 that matches the systems the item touches. Not sections 0–4 and not `TODO.md`.
- **Both:** the target mod's `README.md` and `CLAUDE.md` (if it has one). For a bug in `SupermarketFixes`, that mod's `CLAUDE.md` is large: read its overview and Status headings, then only the section for that bug and the "Game reference" sections the bug needs (find them with `grep -n '^#'`). The same applies to any other mod `CLAUDE.md` over about 20 KB (`EmployeeClothes`, `ExtraComputers`, `RestockerDoubleCarry`, `ClearTags`).
- Anything else, such as dumps, other mods' docs or `TODO.md`, only when the work needs it.

## Input

You receive one of:

- **A task name**, such as `bug #3` or `improvement #5`. Match it case-insensitively against the entries in `TODO.md` (`Bug #N` / `Improvement #N`).
- **`start` or `continue`**: take the first actionable entry in `TODO.md` that is not yet reviewed (see below).
- Nothing or something unclear: ask the user which item to review.

Read `TODO.md` fresh every time: it holds the user's requests and, in each entry's **Status** block, the per-item state, code location and review record. The states and rules are in `CLAUDE.md` section 1.

### Item states

| State in `TODO.md` | Meaning | Orchestrator action |
|---|---|---|
| `open` | No code yet | **Eligible for implementation** (step 0b), then review. |
| `in-progress` | Code partly written | **Eligible.** The "Code" column says where it lives. It may be finished or only half done; step 0b decides. |
| `implemented` | Built, not code-reviewed, pending the user's in-game test | **Eligible for review** (step 1): this is what a review request is for. |
| `reviewed` | **Code review only**, pending the user's in-game test | Skip, unless the user names the item explicitly. Before skipping, run `tools/modhash.sh <Code paths>`: if the hash differs from the stored one, the review is stale. Set the state back to `implemented` and treat the item as eligible. |
| `needs-rework` | The in-game test failed or the user asked for changes | **Eligible for implementation**: the implementer makes the requested changes first (step 0b), then review. |
| `done YYYY-MM-DD` | Tested in-game **and** accepted by the user | Skip. |

`reviewed` means only "code review passed". `done` means "tested and confirmed by the user". The orchestrator writes only `implemented` and `reviewed`; it never writes `done`.

For `start` or `continue`, take the first item in `TODO.md` order whose state is `open`, `in-progress`, `implemented` or `needs-rework`, or `reviewed` with a stale hash, skipping items that aren't actionable (below). Tell the user which one you picked before spawning anything. If none qualifies, say so and stop.

**Not actionable, skip and tell the user why:**

- Entries that are questions rather than code tasks (for example Bug #4, how to collect logs). The user answers those with the agent directly.
- Entries that can't be implemented from what's written (for example no reproduction data, or the request is too vague to define the expected behaviour). Say what information is missing. If the user named the item explicitly, ask them for it instead of guessing.

## The loop

### 0a. Gather intent (you do this, briefly)

From the `TODO.md` entry, extract the **intent only**:

- the bug symptom or the feature as the user wrote it,
- the expected behaviour, including multiplayer expectations (host is the Photon master on Linux/Proton with mods; the guest is usually unmodded),
- the mod folder where the change lives (from the `[IN PROGRESS: ...]` pointer, or from CLAUDE.md section 2).

Don't read the implementation to form opinions, and don't pass on any theories you pick up. The reviewer must form its own opinion.

### 0b. Is it implemented? (decide cheaply, don't read the code)

Go to **step 1 (review)** when the state is `implemented` or stale-`reviewed`, or a finished-looking `in-progress` item, and nothing in the entry's Status block or the mod's `CLAUDE.md` status section says the work is incomplete. Go to **step 0c (implement)** when:

- the state is `open`;
- the state is `needs-rework`;
- the `Code` line is missing, or the folder or file it points to doesn't exist;
- the `Waiting on` line or the mod's `CLAUDE.md` status section describes the work as partial, stubbed, placeholder, not building, or with open TODOs.

If you can't tell, go to step 1. The reviewer can also report that the work is incomplete (`VERDICT: INCOMPLETE`, see step 1), which sends you to step 0c.

### 0c. Implement (new or half-done work)

Spawn an **implementer** subagent (`general-purpose`, `model: "sonnet"`) with:

- the intent (symptom/feature as the user wrote it, expected behaviour, multiplayer expectations) and the target location per `CLAUDE.md` section 2 (a bug goes in `SupermarketFixes/src/Patches/`; an improvement gets its own new top-level mod folder with a short PascalCase name), plus the entry's request text verbatim, and an instruction to follow the reading list first;
- for a half-done item: where the existing work lives, and an instruction to **read it first, keep what is sound, and finish or redo the rest**; for `needs-rework`, the user's feedback verbatim (from `TODO.md`);
- for a reviewer-reported `INCOMPLETE`: the reviewer's list of missing parts, verbatim;
- an instruction to follow the repo's `CLAUDE.md` and `README.md` as written: read only what the reading list names (the request text is in your prompt, so it doesn't read `TODO.md`); write the root cause or mechanism in the patch doc comment; create or update the mod's `README.md` (required) and `CLAUDE.md` (optional, only if there is implementation knowledge worth keeping); add a new improvement to the mods table in the root `README.md`; follow the plugin conventions (config entries, `Enabled` switch, fail open, GUID and version sync); deploy to the plugins root;
- an instruction to build (`dotnet build -c Release` in the mod folder) and fix all compile errors before reporting;
- an instruction to **commit its work** with `git add <paths>` and a brief title-only commit message (no body, no tags such as `feat:`);
- an instruction **not to edit `TODO.md`** and not to touch other mods (a bug fix may touch the shared `Plugin.cs` of SupermarketFixes only);
- the required output format: what was implemented, files created or changed, the config entries added, the build result, and anything it couldn't resolve.

Record the implementer's agent id; the review loop reuses it.

When the implementer reports, update the item **yourself** (you own the Status blocks in `TODO.md`): set `Status: implemented`, fill in `Code`, and set `Waiting on: Review loop`. Never touch the user's request text. Commit with a brief title-only message. Then continue to step 1.

If the implementer reports it couldn't finish, or the request needs a decision from the user (see Rules), stop and report to the user instead of going on to review.

### 1. Spawn the reviewer

First decide whether a review is needed. **Skip it** (the state stays `implemented`; tell the user why) when `git diff --stat` and `git diff` of the item's code show no logic change: only docs, comments, version strings, or constant and default values. Anything that touches a patch, Photon/RPC code or shared state gets the full loop, however small the diff.

Use the Agent tool (`general-purpose`, `model: "opus"`). Give it a self-contained prompt with:

- the intent (symptom/feature, expected behaviour, multiplayer expectations) and the mod folder;
- an instruction to follow the **reading list** (below) before judging;
- an instruction to review **the code as it exists on disk**: the patch file(s), `Plugin.cs`, the csproj, config entries, and whether the docs match the behaviour;
- an instruction to judge whether the change **correctly and safely achieves the intent**, including: failing open on exceptions, behaviour when the config entry is off, the `Enabled` master switch, multiplayer and desync risk, IL2CPP pitfalls from CLAUDE.md section 5, and version sync between `Plugin.Version` and the csproj;
- an instruction that it **may read dumps and run read-only commands, and may build to check compilation, but must not edit any source files**;
- the output format. The reviewer writes its **full report to a file** (`<scratchpad>/review-<item>-r<round>.md`; you give it the full path) and replies to you with only: the file path, the number of findings per severity, and the verdict line. In the file, findings are **ranked by severity** (blocker / major / minor / nit), numbered (`F1`, `F2`, ...), each with file and line, the concrete failure scenario, and a suggested direction. The verdict is one of:
  - `VERDICT: APPROVED`: no blocker or major finding is open (minors and nits may be listed);
  - `VERDICT: CHANGES REQUIRED`: at least one blocker or major finding is open;
  - `VERDICT: INCOMPLETE`: the intent isn't fully implemented yet (missing features, stubs, placeholders, or it doesn't build); list the missing parts.

Don't include: your reasoning, what you expect it to find, which areas to focus on or ignore, or any hint that the change is safe or risky.

Record the reviewer's agent id so you can continue it with SendMessage.

### 2. Route the result

Don't read or retell the findings yourself; hand the report file over by path.

- `APPROVED` with **no findings at all**: go to step 5.
- `INCOMPLETE`, and you haven't implemented in step 0c yet: go to step 0c with the reviewer's list of missing parts, then return to step 1 and ask the same reviewer to re-review (SendMessage). Implementation from step 0c doesn't count as a review round. If it is `INCOMPLETE` again after step 0c, treat it as `CHANGES REQUIRED`.
- `CHANGES REQUIRED`: spawn an implementer (below) with the report. Every finding goes to it, blockers and majors first.
- `APPROVED` with only minor or nit findings: one implementer round for them (below). Afterwards, ask the reviewer for a diff-only check (step 3) **only if** the implementer's response says the round changed more than comments, docs or names; otherwise go straight to step 5. A minor or nit that the implementer rejects with a reason isn't escalated; list it in the final report to the user.

Implementer round: spawn an **implementer** (`general-purpose`, `model: "sonnet"`) the first time, or continue the one from step 0c with SendMessage. Before it starts, record `git rev-parse HEAD` (the round's base commit). Give it:

- the same intent and mod folder;
- the path of the reviewer's report file (it reads the findings there; you don't paste them);
- an instruction to follow the reading list;
- an instruction to evaluate each finding critically, **apply the justified ones**, rebuild (`dotnet build -c Release` in the mod folder) and fix any compile errors, and keep the mod's `README.md`, `CLAUDE.md` and patch doc comment current when behaviour changes;
- permission to **disagree** with a finding, but only with a concrete reason;
- an instruction to **commit** each applied round (`git add <paths>`, brief title-only message, no body or tags);
- an instruction **not to edit `TODO.md`** and not to touch other mods;
- the output format: a point-by-point response written to `<scratchpad>/review-<item>-r<round>-response.md` (per finding `fixed`, `disagree: <reason>` or `already handled: <where>`), and a short reply to you with the file path, the build result, the files changed, and whether the round changed anything beyond comments, docs or names.

Record the implementer's agent id. On later rounds, continue the same implementer with SendMessage instead of spawning a new one.

### 3. Ask the reviewer to re-review

Send the response file path and the round's base commit to the reviewer with SendMessage (same agent, so it keeps its context). Tell it to:

- review the **diff**, `git diff <base commit>..HEAD -- <mod folder>`, not the whole mod again; read a full file only where the diff touches it or where it needs the surrounding code to judge a regression;
- check each previously reported finding as resolved, still open, or rejected-with-valid-reason, and check the response for minors and nits;
- not rebuild unless the implementer's reported build is missing or failed;
- write its report to a new file and end its reply with a fresh `VERDICT:` line, using the same definitions as step 1.

If a reviewer can't be continued (SendMessage unavailable or the agent is gone), spawn a new one (again `model: "opus"`) with the intent and the paths of the earlier reports and responses, and ask it to review the current code from scratch.

### 4. Repeat

Go back to step 2 while the verdict is `CHANGES REQUIRED`. You are only the courier: pass paths along unedited, and don't take sides in a disagreement.

**There is no limit on the number of rounds.** The loop ends only on agreement, or when the two agents keep disputing the same point across two rounds. In that case stop, don't mark the item, summarise the open disagreement for the user with both positions, and let them decide.

**Agreement** means: the reviewer says `VERDICT: APPROVED` **after re-reviewing the code that resulted from the fixes**, and every blocker and major finding it ever raised is resolved: fixed, already handled, or rejected with a reason the reviewer accepted. Minors and nits must each be fixed, already handled, or rejected by the implementer with a stated reason (those go to the user in the final report). **An item is never marked `reviewed` while a blocker or major finding is open.** An approval given on code that was changed afterwards doesn't count, except for a minors-only round that changed nothing beyond comments, docs or names.

### 5. Close out

When both agree:

1. Re-read `TODO.md` (it may have changed during the loop).
2. Compute the hash of the final code: `tools/modhash.sh <paths from the Code column>`.
3. Update the entry's Status block in `TODO.md`: `Status: reviewed`, `Review: YYYY-MM-DD (today), <rounds> rounds, hash \`<hash>\``, and `Waiting on: In-game test` (plus any logs to send). Example:
   ```
   **Status:** reviewed
   **Code:** `SupermarketFixes/src/Patches/RackMoveFix.cs`
   **Review:** 2026-11-15, 3 rounds, hash `eb7de42e7b56`
   **Waiting on:** In-game test + host F8 log.
   ```
4. Leave the user's request text and every other entry untouched.
5. Commit the `TODO.md` change with a brief title-only message (for example `Mark Improvement #8 reviewed`). Make sure the implementer's code is committed too; if not, commit it by path.
6. Don't touch `done` entries. Only the user, after an in-game test, makes an entry done. Don't rewrite or reorder other entries.
7. Report to the user:
   - which item, whether it was implemented from scratch, finished from a half-done state, or already complete, and how many review rounds it took;
   - the findings that were raised and fixed (short list);
   - any finding the implementer disagreed with and how it was resolved, including every minor or nit it rejected (read the response files for this; they are the one place you read findings);
   - files that changed;
   - the in-game test steps and log lines the user should send back (take them from the mod's `CLAUDE.md`; if it has none or they're missing, say so).

If the user asked for `start` or `continue`, finish after one item and wait for the next command. Don't chain items unless told to. End your report by suggesting `/clear` before the next item, so one item's context isn't carried into the next.

## Rules

- **Reviewer never edits source. Implementer never reviews or edits `TODO.md`. You edit only the Status blocks in `TODO.md`.** The orchestrator never writes mod code itself, even for a small fix; it always goes through an implementer subagent launched with `model: "sonnet"`.
- **Models are fixed:** every implementer runs on `sonnet` and every reviewer on `opus`. Always pass `model` explicitly on each Agent call, including respawns; never use the default.
- Work on one item at a time. Two implementers editing the shared `SupermarketFixes/src/Plugin.cs` or the root `README.md` at once would conflict.
- If the implementer changes code after you've recorded `reviewed`, the stored hash goes stale; the next run catches it.
- Keep the reviewer's brief free of implementation details and verdict hints (CLAUDE.md section 2b). The implementer gets the findings, not your opinions.
- You can't run the game. A review pass is never confirmation that a fix works, so the item gets `reviewed`, not `done`.
- If an implementer change would touch another mod, add cross-mod dependencies, or change plugin GUIDs or public settings in a breaking way, stop and ask the user.
- If the loop uncovers new game knowledge that most future sessions would need, have the implementer add it to the root `CLAUDE.md` (per its section 2a). Knowledge about a single mod goes in that mod's docs.
- Never close out an item with an open blocker or major finding. Minors and nits always go to the implementer, but they don't hold the loop open (see step 2 and the agreement rule in step 4).
- There is no round limit. Don't invent one.
- Findings and responses travel as files, by path. Be terse in relays and don't add commentary. Continue existing agents with SendMessage rather than respawning them, so their context stays cached.
