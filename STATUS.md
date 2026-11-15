# Status of TODO items

`TODO.md` is the user's request list. This file tracks where each item stands. Agents own it; the user can edit it too.

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
- The `TODO.md` line carries **one** status prefix that mirrors the state here: `[IN PROGRESS: <pointer>]`, `[REVIEWED YYYY-MM-DD: <pointer>]`, `[DONE YYYY-MM-DD]` (none for `open`). Never stack prefixes. Update this table and the prefix in the same change.
- Bug items live in `SupermarketFixes/`; their hash covers only the patch file named in "Code" (shared files such as `Plugin.cs` aren't hashed).

## Items

| Item | State | Code | Reviewed (date, rounds, hash) | Awaiting / notes |
|---|---|---|---|---|
| Bug #1 | reviewed | `SupermarketFixes/src/Patches/RackMoveFix.cs` | 2026-11-15, 1 round (follow-up review of v0.7.0), 0a5ff690803c | In-game test + host F8 log; `DematerializeRackedBoxesOnHost` stays off until tested with an unmodded guest |
| Bug #2 | reviewed | `SupermarketFixes/src/Patches/GhostHeldItemFix.cs` | 2026-11-15, 1 round (follow-up review), d48d7436015a | In-game test + logs, incl. the host's `[BoxDiag] DropBox_RPC` lines |
| Bug #3 | reviewed | `SupermarketFixes/src/Patches/StuckBoxFix.cs` | 2026-11-15, 1 round (follow-up review), 2858032d1b39 | In-game test + logs |
| Bug #4 | open | — | — | Guest crash logs (2026-11-15) analysed: hard kill, no managed or Unity crash trace. Need the guest's Windows crash folder + Event Viewer entry |
| Bug #5 | needs-rework | `SupermarketFixes/src/Patches/BoxPlaceFix.cs` | 2026-11-15, 1 round, 5066b4084b02 | In-game test 2026-11-15: still happening, no improvement |
| Bug #6 | open | — | — | In-game 2026-11-15: still happening |
| Improvement #1 | needs-rework | `PriceMarkupKeeper/` | — | Confirmed in-game 2026-11-15; rework: apply the rule to newly licensed products right away |
| Improvement #2 | done 2026-11-14 | — | — | |
| Improvement #3 | needs-rework | `PlayerCrouch/` | — | Confirmed in-game 2026-11-15; rework: other players see no crouch pose (mod on both machines) |
| Improvement #4 | in-progress | `SelfCheckoutSpeed/` | — | Rework v1.1.0 written (host-authoritative speed, guest catch-up, text field, employee help); review pending |
| Improvement #5 | needs-rework | `RestockerDoubleCarry/` | 2026-11-15, rework v1.1.1, 1 round, be2d923dd34d | In-game 2026-11-15: top box floats when the bottom one is set down |
| Improvement #6 | done 2026-11-15 | `FurnitureMoveCancel/` | — | Confirmed in-game; v1.0.2 defaults `VerboseLogging` to off |
| Improvement #7 | in-progress | `ExtraComputers/` | — | v1.2.1 (review round 4 applied). v1.2.0 after the first in-game test (2026-11-15): the furniture prefab failed to build (no donor furniture), so the box opened into nothing and the icon was the drawn placeholder. Rebuilt from a copy of the store computer; boxes refuse to open if the build still fails. Needs review, then in-game test per the mod's CLAUDE.md |
| Improvement #8 | done 2026-11-15 | `GridModeDefault/` | — | Confirmed in-game; v1.0.2 defaults `VerboseLogging` to off |
| Improvement #9 | needs-rework | `BulkShelfTransfer/` | 2026-11-15, 1 round, 45be1793f004 | Rework 2026-11-15: Shift-click with online-order paper bags |
| Improvement #10 | needs-rework | `PlayerDoubleCarry/` | 2026-11-15, 1 round, 8e81b678aa84 | Rework 2026-11-15: buy a second box at the wholesale store while holding one |
| Improvement #11 | open | — | — | |
| Improvement #12 | open | — | — | |
| Improvement #13 | open | — | — | |
| Improvement #14 | open | — | — | |
| Improvement #15 | open | — | — | |

Example of a reviewed row:
`| Bug #1 | reviewed | SupermarketFixes/src/Patches/RackMoveFix.cs | 2026-11-15, 3 rounds, eb7de42e7b56 | In-game test + host F8 log |`
