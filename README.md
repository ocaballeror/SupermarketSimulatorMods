# Supermarket Simulator Mods

BepInEx mods for **Supermarket Simulator** (Nokta Games, Steam app 2670630). The repo has two goals:

1. **Fix game bugs.** They mostly show up in online co-op. All bug fixes ship together in one mod, **SupermarketFixes**.
2. **Add improvements.** Each improvement is its own small, standalone mod, so it can be published separately for other players.

What each mod does, and where it stands (open, in progress, implemented, reviewed, done), is tracked per request in [`TODO.md`](TODO.md).

## Mods

Each mod folder has its own `README.md` (what it does, its settings, who needs it in multiplayer). Mods that need development notes also have a `CLAUDE.md` for coding agents.

| Mod | Request | What it does |
|---|---|---|
| [Supermarket Fixes](SupermarketFixes) | Bugs | All bug fixes: rack-move ghost boxes, ghost items in hands, stuck boxes, shelf placing, floating boxes, janitor mop alignment and stand spot |
| [Paint Can Capacity](PaintCanCapacity) | Improvement #2 | Paint cans last longer (2x by default, configurable) |
| [Price Markup Keeper](PriceMarkupKeeper) | Improvement #1 | Prices keep your global markup when market prices change; new products start at it |
| [Player Crouch](PlayerCrouch) | Improvement #3 | Hold Ctrl to crouch; the character model crouches too (visible to modded players) |
| [Self Checkout Speed](SelfCheckoutSpeed) | Improvement #4 | Faster self-checkout customers and employee help (configurable, follows the host) |
| [Restocker Double Carry](RestockerDoubleCarry) | Improvement #5 | High-level restockers fetch two boxes per trip |
| [Furniture Move Cancel](FurnitureMoveCancel) | Improvement #6 | Escape puts moved furniture back where it was |
| [Extra Computers](ExtraComputers) | Improvement #7 | Buyable extra computers, each with its own screen |
| [Grid Mode Default](GridModeDefault) | Improvement #8 | Grid mode is on by default when placing furniture |
| [Bulk Shelf Transfer](BulkShelfTransfer) | Improvement #9 | Shift+click moves a whole shelf slot into or out of a box, paper bag or crate |
| [Player Double Carry](PlayerDoubleCarry) | Improvement #10 | Carry two boxes; neither can be opened until one is put down |
| [Clear Tags](ClearTags) | Improvement #11 | Box tags are 2x bigger (configurable); a hover text on box, shelf and rack tags shows product, quantity and box price (local only) |
| [Auto Courier Load](AutoCourierLoad) | Improvement #12 | The returning courier picks up closed, name-tagged delivery bags near his parking space and leaves when his trunk covers every active order or a deadline is close (host only) |
| [Product Highlight](ProductHighlight) | Improvement #13 | Holding a box outlines every shelf, fridge and freezer labeled for its product; holding an order bag outlines the stocked products the order still needs (local only) |
| [Pack Up Products](PackUpProducts) | Improvement #14 | Right click a shelf product with empty hands to pack it into a new box of the right size (free; guests need the mod on the host too) |
| [Employee Clothes](EmployeeClothes) | Improvement #15 | Pick the colour of each employee type's clothes from the computer's Customization tab ($500 each time; the host chooses, guests with the mod see it) |

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

**Multiplayer note:** some mods only work if the machine where the problem happens runs the mod; for example, a bug seen on the guest is fixed on the guest. Each mod's README says whether the host, the guest, or both need it.

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

```
README.md                this file: project overview
CLAUDE.md                instructions for coding agents: workflow, environment, tools, shared game knowledge
TODO.md                  the requests (bugs and improvements) and the status of each
REVIEW_ORCHESTRATOR.md   agent definition for the implement-and-review loop
SupermarketFixes/        the bug-fix mod (one BepInEx plugin, one patch file per bug)
<ModName>/               one folder per improvement mod (see the Mods table); each has
                         README.md (required) and CLAUDE.md (optional), src/, and a csproj
tools/
  Il2CppDump/            dumps native code (disassembly + Cpp2IL ISIL) for chosen game types
  isil.sh                prints one method's ISIL from a dump, condensed
  callers.sh             lists every method that calls a given method
  modhash.sh             short hash of a mod's code, to detect changes after a review
  readconst.py           reads a float/int literal from GameAssembly.dll by address
  bindings.py            lists the game's default input bindings
decompiled/              generated reference dumps (git-ignored, regenerate after game updates)
  Assembly-CSharp/       interop stubs decompiled with ilspycmd: class/field/method signatures
  native-all/            ISIL dumps of every Assembly-CSharp type: what the methods actually do
```

## Collecting logs (crash and bug reports)

Logs are needed to debug crashes, especially guest crashes. After a crash, **before relaunching**, copy these files. Relaunching overwrites `Player.log`, although the previous run is kept as `Player-prev.log`.

| Machine | Unity player log | Crash dumps |
|---|---|---|
| Windows (native) | `%USERPROFILE%\AppData\LocalLow\Nokta Games\Supermarket Simulator\Player.log` (and `Player-prev.log`) | `%TEMP%\Nokta Games\Supermarket Simulator\Crashes\` |
| macOS (native) | `~/Library/Logs/Nokta Games/Supermarket Simulator/Player.log` (and `Player-prev.log`) | `~/Library/Logs/DiagnosticReports/` (files mentioning Supermarket Simulator) |
| Linux host (Proton) | `~/.local/share/Steam/steamapps/compatdata/2670630/pfx/drive_c/users/steamuser/AppData/LocalLow/Nokta Games/Supermarket Simulator/Player.log` | same prefix, `AppData/Local/Temp/Nokta Games/Supermarket Simulator/Crashes/` |

Every mod's own diagnostic lines (tags such as `[GhostHeld]`, `[AutoCourier]`) are written only when its `VerboseLogging` setting is on (`[Diagnostics]` section of its cfg in `BepInEx/config/`; off by default). Turn it on before reproducing a problem.

With BepInEx installed, also collect `BepInEx/LogOutput.log` and `BepInEx/ErrorLog.log` from the game folder. `ErrorLog.log` captures fatal .NET errors such as access violations.

During online play the game writes its own `[BoxDiag]` lines to `Player.log`, even without mods: box pickups, host grant or refusal, timeouts, and `WATCHDOG MISMATCH` when a held box is out of sync. For box-related bugs, send those lines from the machine where the bug happened.
