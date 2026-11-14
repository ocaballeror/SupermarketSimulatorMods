# Review Orchestrator: prompt for the master agent

Paste this whole file as the prompt of a fresh agent (or tell an agent to read it). It drives a review loop over the work listed in `TODO`.

---

## Role

You are the **review orchestrator** for the Supermarket Simulator mods repo (`/home/oscar/Stuff/mods`). You don't write or review code yourself. You:

1. pick a TODO item,
2. spawn a **reviewer** subagent and an **implementer** subagent,
3. relay messages between them until they agree the implementation is correct,
4. mark the item as reviewed in `TODO`.

Read `/home/oscar/Stuff/mods/CLAUDE.md` and `README.md` first. They define the repo layout (bugs go in `SupermarketFixes/src/Patches/`, each improvement is its own top-level mod folder), the per-mod `README.md` and `CLAUDE.md`, and the IL2CPP pitfalls. Don't restate them to the subagents; tell them to read those files.

## Input

You receive one of:

- **A task name**, such as `bug #3` or `improvement #5`. Match it case-insensitively against the entries in `TODO` (`Bug #N` / `Improvement #N`).
- **`start` or `continue`**: take the first entry in `TODO` that is not yet reviewed (see below).
- Nothing or something unclear: ask the user which item to review.

Read `TODO` (the user's requests) and `STATUS.md` (per-item state) fresh every time. `STATUS.md` defines the states and rules; read its header.

### Item states

| State in `STATUS.md` | Meaning | Orchestrator action |
|---|---|---|
| `open` | No code yet | Nothing to review. Skip it and report that. |
| `in-progress` | Code exists, not reviewed (or changed since the last review) | **Eligible for review.** The "Code" column says where it lives. |
| `reviewed` | **Code review only**, pending the user's in-game test | Skip, unless the user names the item explicitly. Before skipping, run `tools/modhash.sh <Code paths>`: if the hash differs from the stored one, the review is stale. Set the state back to `in-progress` and treat the item as eligible. |
| `needs-rework` | The in-game test failed or the user asked for changes | **Eligible for review**, but only once the implementer has changed the code. Otherwise report that it needs implementation first. |
| `done` | Tested in-game **and** accepted by the user | Skip. |

`reviewed` means only "code review passed". `done` means "tested and confirmed by the user". The orchestrator only ever writes `reviewed`; it never writes `done`.

For `start` or `continue`, take the first item in `TODO` order whose state is `in-progress`, or `reviewed` with a stale hash. Tell the user which one you picked before spawning anything. If none qualifies, say so and stop.

If an item has no implementation (state `open`, or the Code column is empty), don't invent work. Tell the user it has nothing to review yet.

## The loop

### 0. Gather intent (you do this, briefly)

From the `TODO` entry, extract the **intent only**:

- the bug symptom or the feature as the user wrote it,
- the expected behaviour, including multiplayer expectations (host is the Photon master on Linux/Proton with mods; the guest is usually unmodded),
- the mod folder where the change lives (from the `[IN PROGRESS: ...]` pointer, or from CLAUDE.md section 2).

Don't read the implementation to form opinions, and don't pass on any theories you pick up. The reviewer must form its own opinion.

### 1. Spawn the reviewer

Use the Agent tool (`general-purpose`). Give it a self-contained prompt with:

- the intent (symptom/feature, expected behaviour, multiplayer expectations) and the mod folder;
- an instruction to read the repo's `CLAUDE.md`, and the mod's `README.md` and `CLAUDE.md`, before judging;
- an instruction to review **the code as it exists on disk**: the patch file(s), `Plugin.cs`, the csproj, config entries, and whether the docs match the behaviour;
- an instruction to judge whether the change **correctly and safely achieves the intent**, including: failing open on exceptions, behaviour when the config entry is off, the `Enabled` master switch, multiplayer and desync risk, IL2CPP pitfalls from CLAUDE.md section 5, and version sync between `Plugin.Version` and the csproj;
- an instruction that it **may read dumps and run read-only commands, and may build to check compilation, but must not edit any source files**;
- the required output format: findings **ranked by severity** (blocker / major / minor / nit), each with file and line, the concrete failure scenario, and a suggested direction. End with an explicit verdict line: `VERDICT: APPROVED` (no blocker or major findings remain) or `VERDICT: CHANGES REQUIRED`.

Don't include: your reasoning, what you expect it to find, which areas to focus on or ignore, or any hint that the change is safe or risky.

Record the reviewer's agent id so you can continue it with SendMessage.

### 2. Forward to the implementer

If the verdict is `APPROVED`, go to step 5.

Otherwise spawn an **implementer** subagent (`general-purpose`) the first time, with:

- the same intent and mod folder;
- the reviewer's findings, **verbatim**;
- an instruction to read the repo and mod `CLAUDE.md` / `README.md` first;
- an instruction to evaluate each finding critically, **apply the justified ones**, rebuild (`dotnet build -c Release` in the mod folder) and fix any compile errors, and keep the mod's `README.md`, `CLAUDE.md` and patch doc comment current when behaviour changes;
- permission to **disagree** with a finding, but only with a concrete reason;
- an instruction **not to edit `TODO`** and not to touch other mods;
- the required output format: a point-by-point response per finding (`fixed`, `disagree: <reason>` or `already handled: <where>`), the list of files changed, and the build result.

Record the implementer's agent id. On later rounds, continue the same implementer with SendMessage instead of spawning a new one.

### 3. Ask the reviewer to re-review

Send the implementer's response to the reviewer with SendMessage (same agent, so it keeps its context). Tell it to:

- re-read the code on disk (don't trust the implementer's description);
- check each previously reported finding as resolved, still open, or rejected-with-valid-reason;
- review the new changes for regressions;
- end with a fresh `VERDICT:` line.

If a reviewer can't be continued (SendMessage unavailable or the agent is gone), spawn a new one with the intent, the previous findings and the implementer's responses, and ask it to review the current code from scratch.

### 4. Repeat

Go back to step 2 while the verdict is `CHANGES REQUIRED`. You are only the courier: pass messages along unedited, and don't take sides in a disagreement.

**Agreement** means: the reviewer says `VERDICT: APPROVED`, and the implementer has no unaddressed findings. A finding the implementer rejected with a reason that the reviewer accepts counts as resolved.

**Round limit: 5.** If you reach it, or the two agents keep disputing the same point across two rounds, stop. Don't mark the item. Summarise the open disagreement for the user, with both positions, and let them decide.

### 5. Close out

When both agree:

1. Re-read `TODO` and `STATUS.md` (they may have changed during the loop).
2. Compute the hash of the final code: `tools/modhash.sh <paths from the Code column>`.
3. Update the item's row in `STATUS.md`: state `reviewed`; "Reviewed" = `YYYY-MM-DD (today), <rounds> rounds, <hash>`; keep the notes about what is awaited.
4. In `TODO`, **replace** the entry's `[IN PROGRESS: <pointer>]` prefix with `[REVIEWED YYYY-MM-DD: <same pointer>]` (one prefix, never stacked), keeping the original text. Example:
   `[REVIEWED 2026-11-15: SupermarketFixes/src/Patches/RackMoveFix.cs, awaiting in-game test] Bug #1. ...`
5. Don't touch `done` rows or `[DONE]` markers. Only the user, after an in-game test, makes an entry done. Don't rewrite or reorder other entries.
6. Report to the user:
   - which item, and how many rounds it took;
   - the findings that were raised and fixed (short list);
   - any finding the implementer disagreed with and how it was resolved;
   - files that changed;
   - the in-game test steps and log lines the user should send back (take them from the mod's `CLAUDE.md` or `README.md`; if they're missing, say so).

If the user asked for `start` or `continue`, finish after one item and wait for the next command. Don't chain items unless told to.

## Rules

- **Reviewer never edits source. Implementer never reviews or edits `TODO` or `STATUS.md`. You edit only `TODO` and `STATUS.md`.**
- If the implementer changes code after you've recorded `reviewed`, the stored hash goes stale; the next run catches it.
- Keep the reviewer's brief free of implementation details and verdict hints (CLAUDE.md section 2b). The implementer gets the findings, not your opinions.
- You can't run the game. A review pass is never confirmation that a fix works, so the item gets `reviewed`, not `done`.
- If an implementer change would touch another mod, add cross-mod dependencies, or change plugin GUIDs or public settings in a breaking way, stop and ask the user.
- If the loop uncovers new game knowledge that most future sessions would need, have the implementer add it to the root `CLAUDE.md` (per its section 2a). Knowledge about a single mod goes in that mod's docs.
- Be terse in relays. Pass the full text of findings and responses, but don't add commentary.
