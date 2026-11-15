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
| Bug #1 | reviewed | `SupermarketFixes/src/Patches/RackMoveFix.cs` | 2026-11-15, 1 round, 9f3292ae76c2 | In-game test + host F8 log (v0.6.0+) |
| Bug #2 | reviewed | `SupermarketFixes/src/Patches/GhostHeldItemFix.cs` | 2026-11-15, 2 rounds, 50bfe90a93e8 | In-game test + logs (v0.6.1) |
| Bug #3 | reviewed | `SupermarketFixes/src/Patches/StuckBoxFix.cs` | 2026-11-15, 1 round, 13bfc34905b6 | In-game test + logs |
| Bug #4 | open | — | — | Question, not a code task: answer how to collect guest crash logs |
| Bug #5 | in-progress | `SupermarketFixes/src/Patches/BoxPlaceFix.cs` | — | In-game test + logs |
| Bug #6 | open | — | — | No data to reproduce; may be related to Bug #1 |
| Improvement #1 | in-progress | `PriceMarkupKeeper/` | — | In-game test |
| Improvement #2 | done 2026-11-14 | — | — | |
| Improvement #3 | in-progress | `PlayerCrouch/` | — | In-game test |
| Improvement #4 | done 2026-11-14 | — | — | |
| Improvement #5 | in-progress | `RestockerDoubleCarry/` | — | In-game test |
| Improvement #6 | in-progress | `FurnitureMoveCancel/` | — | In-game test |
| Improvement #7 | in-progress | `ExtraComputers/` | — | In-game test |
| Improvement #8 | open | — | — | |
| Improvement #9 | open | — | — | |
| Improvement #10 | open | — | — | |

Example of a reviewed row:
`| Bug #1 | reviewed | SupermarketFixes/src/Patches/RackMoveFix.cs | 2026-11-15, 3 rounds, eb7de42e7b56 | In-game test + host F8 log |`
