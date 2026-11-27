# AutoLights: notes for agents

Player-facing info is in [README.md](README.md). Shared rules and game internals are in the root [CLAUDE.md](../CLAUDE.md). The mechanism write-up is the doc comment at the top of `src/LightsTimer.cs`; this file summarises it.

## Status

v1.0.0: new mod (request: "turn on the lights automatically at 7pm"). Written and built, **not tested in-game**. No Harmony patches: a per-frame `LightsBehaviour` (added with `AddComponent` in `Load()`, touches no game state there) calls `LightsTimer.Tick()`.

## Game mechanism (verified in the dumps)

- **Clock:** `DayCycleManager` (`NoktaSingleton`). `CurrentHour` (+0xA0), `CurrentMinute` (+0x9C), `AM` (+0xA8) form a **12-hour** clock: `SetupDay`/`StartNextDay` set hour = `m_DayStartingTime` (+0x28), AM = true; in `UpdateGameTime`, when hour reaches 12 `AM` becomes false, and when it reaches 13 it drops to 1. So 19:00 = hour 7 with `AM == false`. `AM` never flips back, and hour 12 with `AM == false` is noon (midnight would read the same, if a day ever ran that long). The mod converts to minutes since midnight. `CurrentDay` (int) lives in the game-data object, not in the manager. The in-day float time is `m_CurrentTimeInFloat` (+0x94); `m_DayPercentage` (+0xA4) >= 1 ends the day.
- `DayCycle` is a coroutine that ticks every frame on every client (each machine runs its own clock). The master additionally calls `NetworkDayCycleManager.RequestSyncTime_Broadcast` at each full hour (in `UpdateGameTime`). Events on the manager: `OnTimeChanged` (+0xB0, every game minute), `OnFullHour` (+0xB8), `OnDayFinished` (+0xC0), `OnStartedNewDay` (+0xC8). `MarketLightsController` (outside window lights) and `StreetLights` subscribe to these and compare `(hour >= 12 ? hour-12 : hour) >= m_HourToTurningOnLights` with `AM` the same way; they are scene lights, not the furniture lamps.
- **Day ends** only through `FinishTheDay` / `StartNextDay` (sleep), which resets the clock to the start hour. Vanilla has no time skip inside a day, so "sleeping" never crosses 19:00.
- **Lights:** two `NoktaSingletonPunCallbacks` managers, `StoreLightManager` (`m_InteriorLights`, `m_OutdoorLights`, `m_IsOn` +0x38) and `StorageLightManager` (`m_InteriorLights`, `m_IsOn`). `set_TurnOn(bool)`: online (`PhotonNetwork.IsConnected`) it only sends `photonView.RPC("StoreLightSwitchTurnOn_RPC" / "StorageLightSwitchTurnOn_RPC", RpcTarget 3 = AllBuffered, value)`; the RPC (also run locally) sets every lamp (`InteriorSpotLight.set_TurnOn`, `OutdoorSpotLight.set_IsOn`) and `m_IsOn`. Offline it applies directly. Both managers subscribe `TurnOn = false` to `OnStartedNewDay` in `Start`. The state is **not saved** (nothing in the save/load path reads `m_IsOn`); late joiners are synced by `FurnitureManager.SyncCurrentLights_RPC(store, storage)` via `NetworkFurnitureManager.SyncLights_RPC`.
- **Switch:** scene `LightSwitch` (MonoBehaviour, `IInteractable`, `Type` = 8, instant interaction). `m_Section` (+0x28) is `"Store"` or `"Storage"`; `m_Anim` (+0x20) is the lever animator. `InstantInteract()`: `manager.TurnOn = !manager.m_IsOn` for that section, `SFXManager.PlaySwitchSFX(isOn, position)`, `m_Anim.SetBool("On", newState)`, then a small onboarding hook (`OnboardingManager.ShowLightsTutorial` if the lights tutorial step is pending). It needs no player argument, so the mod calls it directly. The lever animation is local (vanilla doesn't sync it either).
- `LightSwitch.RemoteInteract` throws NotImplementedException; don't use it.

## What the mod does

- Host or offline only (`PhotonNetwork.IsConnected && !IsMasterClient` returns).
- Each frame: read the clock. On the first sight of a clock instance (pointer), on a new `CurrentDay`, or after re-enabling, it only records the time. Otherwise a forward step of 0..120 minutes (`MaxStepMinutes`) from before 19:00 to 19:00 or later fires, at most once per `CurrentDay`. A bigger step (save load setting the clock from the start hour to the saved time, a time-skipping mod) or a backward step never fires.
- Fire: `FindObjectsOfType<LightSwitch>()`; for section "Store"/"Storage" whose manager is off **and** has at least one lamp (store: interior + outdoor), call `InstantInteract()`. Both sections are done (storage room lights count as shop lights). No switch means nothing.
- Pitfall: a save loaded just before 19:00 (say 18:59) still fires at 19:00, which is intended.

## Multiplayer reasoning

Host-side only; the game's buffered RPC carries the state to guests and late joiners, so unmodded guests are fine. A guest never acts (its own clock is a local copy). If the host is unmodded, the mod does nothing for it.

## Open questions

- Not run in-game: that `InstantInteract()` called from our behaviour works the same as the player's click (nothing in its ISIL depends on a player or hand), that the lever animation looks right, and that the day start hour is early enough that `MaxStepMinutes` 120 never hides a real crossing.
- If the lights tutorial step is pending, the first automatic flip may trigger `OnboardingManager.ShowLightsTutorial` as a real click would.
- Whether the day ever runs past midnight (hour 12 with AM false would then read as noon again); the once-per-`CurrentDay` guard prevents a second firing.

## In-game test steps

1. Turn on `VerboseLogging` in `BepInEx/config/flamboyanthopper.autolights.cfg`. Place at least one store lamp (and a storage lamp if you want to test that), make sure the lights are off, and play through an evening.
2. At 19:00: expect `[AutoLights] Clock crossed 19:00 ...`, `[AutoLights] Switched the Store lights on`, the lamps on, a switch sound, the lever moved; with a guest connected, the lamps on for them as well. Turn the lights off again: they must stay off.
3. Next day (sleep): lights go off at the day start (game behaviour) and come on again at 19:00.
4. Load a save that is past 19:00: nothing switches (`Watching the clock` line only). Join as a guest mid-evening: nothing happens on the guest.
5. Send back the `[AutoLights]` lines from `BepInEx/LogOutput.log` plus the in-game time at which the lights came on.
