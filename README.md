# Supermarket Simulator Mods

BepInEx mods for **Supermarket Simulator** (Nokta Games, Steam app 2670630). The repo has two goals:

1. **Fix game bugs.** They mostly show up in online co-op. All bug fixes ship together in one mod, **SupermarketFixes**.
2. **Add improvements.** Each improvement is its own small, standalone mod, so it can be published separately for other players.

The work list lives in [`TODO`](TODO): the bugs and improvements requested, and what's done.

## Mods

| Mod | Folder | Kind | Status |
|---|---|---|---|
| Supermarket Fixes | [`SupermarketFixes/`](SupermarketFixes) | All bug fixes from `TODO` | In progress (Bugs #1–#3 and #5 written, awaiting in-game confirmation) |
| Paint Can Capacity | [`PaintCanCapacity/`](PaintCanCapacity) | Improvement #2: paint cans last 2x (configurable) | Done (confirmed in-game) |
| Self Checkout Speed | [`SelfCheckoutSpeed/`](SelfCheckoutSpeed) | Improvement #4: faster self checkout customers (configurable) | Done (confirmed in-game) |
| Furniture Move Cancel | [`FurnitureMoveCancel/`](FurnitureMoveCancel) | Improvement #6: Escape puts moved furniture back | Written, awaiting in-game test |
| Restocker Double Carry | [`RestockerDoubleCarry/`](RestockerDoubleCarry) | Improvement #5: level 10+ restockers fetch two boxes per trip | Written, awaiting in-game test |
| Price Markup Keeper | [`PriceMarkupKeeper/`](PriceMarkupKeeper) | Improvement #1: prices keep the bulk markup when market prices change | Written, awaiting in-game test |
| Player Crouch | [`PlayerCrouch/`](PlayerCrouch) | Improvement #3: hold Ctrl to crouch, character model crouches (synced to modded players) | Written, awaiting in-game test |
| Extra Computers | [`ExtraComputers/`](ExtraComputers) | Improvement #7: buyable extra computers, each with its own screen | Written, awaiting in-game test |

## Game and platform

- Unity 6000.3.6f1, compiled with **IL2CPP** (no managed game DLLs; code lives in native `GameAssembly.dll`).
- Multiplayer is Photon PUN co-op. The host is the Photon master client and simulates employees, customers and deliveries; guests mirror its state.
- Typical test session:
  - **Host:** this Linux PC, running the **Windows build through Proton**, with all mods installed.
  - **Guest:** a **Mac or Windows PC running the native build**, usually without mods.
- Mod loader: **BepInEx 6.0.0-be.785, Unity IL2CPP build** (the .NET 6 runtime ships with it).

## Installing a mod (players)

1. Install BepInEx 6 bleeding edge, **IL2CPP** flavour, for your platform into the game folder. On Linux/Proton, use the Windows build and set the Steam launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
2. Start the game once so BepInEx generates its interop assemblies. The first start takes a while.
3. Copy the mod's `.dll` (for example `SupermarketFixes.dll`) straight into `BepInEx/plugins/`, not into a subfolder.
4. Settings are created on first launch in `BepInEx/config/<mod guid>.cfg`. They can also be edited in-game with BepInExConfigManager.

**Multiplayer note:** some fixes only work if the machine where the bug happens runs the mod. For example, Bug #1 happens on the guest. Each mod's description says whether the host, the guest, or both need it.

## Building

Requires the .NET SDK (any recent version; projects target `net6.0`) and the game with BepInEx installed and launched at least once.

```sh
cd SupermarketFixes
dotnet build -c Release                  # builds and copies the dll into the game's BepInEx/plugins/ (the folder root, no subfolder)
dotnet build -c Release -p:Deploy=false  # build only
dotnet build -c Release -p:GameDir="/path/to/Supermarket Simulator"   # non-default install
```

Projects compile against the game's own `BepInEx/core` and `BepInEx/interop` DLLs. Nothing from the game is committed here.

## Repository layout

Each mod folder has its own `README.md`: what the mod does, its settings, who needs it in multiplayer, and how it works.

```
README.md            this file
CLAUDE.md            detailed instructions for coding agents
TODO                 bugs and improvements to work on (source of truth for tasks)
SupermarketFixes/    bug-fix mod (one BepInEx plugin, one patch file per bug)
PaintCanCapacity/    improvement mods, one folder each (see the Mods table)
SelfCheckoutSpeed/
FurnitureMoveCancel/
PriceMarkupKeeper/
RestockerDoubleCarry/
ExtraComputers/
PlayerCrouch/
tools/
  Il2CppDump/        dumps native code (disassembly + Cpp2IL ISIL) for chosen game types
  isil.sh            prints one method's ISIL from a dump, condensed
  callers.sh         lists every method that calls a given method
  readconst.py       reads a float/int literal from GameAssembly.dll by address
  bindings.py        lists the game's default input bindings
decompiled/          generated reference dumps (git-ignored, regenerate after game updates)
  Assembly-CSharp/   interop stubs decompiled with ilspycmd: class/field/method signatures
  native-all/        ISIL dumps of every Assembly-CSharp type: what the methods actually do
```

## Collecting logs (crash and bug reports)

Logs are needed to debug crashes, especially guest crashes (TODO Bug #4). After a crash, **before relaunching**, copy these files. Relaunching overwrites `Player.log`, although the previous run is kept as `Player-prev.log`.

| Machine | Unity player log | Crash dumps |
|---|---|---|
| Windows (native) | `%USERPROFILE%\AppData\LocalLow\Nokta Games\Supermarket Simulator\Player.log` (and `Player-prev.log`) | `%TEMP%\Nokta Games\Supermarket Simulator\Crashes\` |
| macOS (native) | `~/Library/Logs/Nokta Games/Supermarket Simulator/Player.log` (and `Player-prev.log`) | `~/Library/Logs/DiagnosticReports/` (files mentioning Supermarket Simulator) |
| Linux host (Proton) | `~/.local/share/Steam/steamapps/compatdata/2670630/pfx/drive_c/users/steamuser/AppData/LocalLow/Nokta Games/Supermarket Simulator/Player.log` | same prefix, `AppData/Local/Temp/Nokta Games/Supermarket Simulator/Crashes/` |

With BepInEx installed, also collect `BepInEx/LogOutput.log` and `BepInEx/ErrorLog.log` from the game folder. `ErrorLog.log` captures fatal .NET errors such as access violations.

During online play the game writes its own `[BoxDiag]` lines to `Player.log`, even without mods: box pickups, host grant or refusal, timeouts, and `WATCHDOG MISMATCH` when a held box is out of sync. For box-related bugs, send those lines from the machine where the bug happened.
