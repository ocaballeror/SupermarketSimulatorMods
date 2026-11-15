# Status of TODO items

`TODO` is the user's request list. This file tracks where each item stands. Agents own it; the user can edit it too.

## States

One state per item, in this lifecycle:

| State | Meaning | Set by |
|---|---|---|
| `open` | No code yet | (initial) |
| `in-progress` | Code exists or is being changed; not code-reviewed, or changed since the last review | implementing agent; the orchestrator when a review goes stale |
| `reviewed` | **Code review only**: reviewer and implementer agreed. Pending the user's in-game test | review orchestrator (`REVIEW_ORCHESTRATOR.md`) |
| `needs-rework` | The in-game test failed, or the user asked for changes | whoever records the user's feedback |
| `done` | **Tested in-game and accepted by the user** | only after the user confirms |

Rules:

- `reviewed` is never `done`. A build or a code review isn't an in-game test.
- **Stale reviews:** a `reviewed` row stores a hash of the code that was reviewed (`tools/modhash.sh <paths in "Code">`). If the current hash differs, the code changed after the review: set the state back to `in-progress`.
- The `TODO` line carries **one** status prefix that mirrors the state here: `[IN PROGRESS: <pointer>]`, `[REVIEWED YYYY-MM-DD: <pointer>]`, `[DONE YYYY-MM-DD]` (none for `open`). Never stack prefixes. Update this table and the prefix in the same change.
- Bug items live in `SupermarketFixes/`; their hash covers only the patch file named in "Code" (shared files such as `Plugin.cs` aren't hashed).

## Items

| Item | State | Code | Reviewed (date, rounds, hash) | Awaiting / notes |
|---|---|---|---|---|
| Bug #1 | reviewed | `SupermarketFixes/src/Patches/RackMoveFix.cs` | 2026-11-15, 1 round (follow-up review of v0.7.0), 0a5ff690803c | In-game test + host F8 log; `DematerializeRackedBoxesOnHost` stays off until tested with an unmodded guest |
| Bug #2 | reviewed | `SupermarketFixes/src/Patches/GhostHeldItemFix.cs` | 2026-11-15, 1 round (follow-up review), d48d7436015a | In-game test + logs, incl. the host's `[BoxDiag] DropBox_RPC` lines |
| Bug #3 | reviewed | `SupermarketFixes/src/Patches/StuckBoxFix.cs` | 2026-11-15, 1 round (follow-up review), 2858032d1b39 | In-game test + logs |
| Bug #4 | open | — | — | Question, not a code task: answer how to collect guest crash logs |
| Bug #5 | reviewed | `SupermarketFixes/src/Patches/BoxPlaceFix.cs` | 2026-11-15, 1 round, 5066b4084b02 | In-game test + logs. Trigger path unproven; delete the stale `plugins/SupermarketFixes/` subfolder dll and set `[General] Enabled = true` before testing |
| Bug #6 | open | — | — | No data to reproduce; may be related to Bug #1 |
| Improvement #1 | reviewed | `PriceMarkupKeeper/` | 2026-11-15, 2 rounds, 67292843335b | In-game test (steps in the mod's CLAUDE.md "How to test in-game"): the `[Markup] New day: ...` summary line proves the detour fired; check guest price tags refresh; check `OnlyProductsFollowingMarkup` off override |
| Improvement #2 | done 2026-11-14 | — | — | |
| Improvement #3 | reviewed | `PlayerCrouch/` | 2026-11-15, 2 rounds, 2a49cccf09cd | In-game test + `[Crouch]` log lines (`VCam follows:`, `human=`, `legIK(...)`, `Camera offset target`). Unverified: vcam follows `CinemachineCameraTarget`, humanoid Animator, leg IK weights outside vehicles |
| Improvement #4 | done 2026-11-14 | — | — | |
| Improvement #5 | in-progress | `RestockerDoubleCarry/` | — | In-game test |
| Improvement #6 | in-progress | `FurnitureMoveCancel/` | — | In-game test |
| Improvement #7 | in-progress | `ExtraComputers/` | — | In-game test |
| Improvement #8 | open | — | — | |
| Improvement #9 | open | — | — | |
| Improvement #10 | open | — | — | |

Example of a reviewed row:
`| Bug #1 | reviewed | SupermarketFixes/src/Patches/RackMoveFix.cs | 2026-11-15, 3 rounds, eb7de42e7b56 | In-game test + host F8 log |`
