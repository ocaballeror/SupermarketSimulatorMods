# Agent instructions: Supermarket Simulator mods

Read [README.md](README.md) for the project overview. This file covers how to work here.

## 1. Always start with `TODO`

[`TODO`](TODO.md) (repo root) is the source of truth for what to work on. The user updates it over time, so **read it fresh at the start of every session**; don't rely on memory or older notes.

- Each entry is `Bug #N` or `Improvement #N` followed by the request.
- **Skip entries marked `[DONE]`.**
- An entry marked `[IN PROGRESS]` has work already started. Its note says where; continue from there instead of starting over.
- When you start work on an entry, prefix it with `[IN PROGRESS]` and a short pointer to where the work lives. Example: `[IN PROGRESS: SupermarketFixes/src/Patches/RackMoveFix.cs, awaiting in-game test] Bug #1. ...`
- **Only mark an entry `[DONE]` once the user has confirmed it works in-game.** A successful build or code review isn't confirmation. Prefix it as `[DONE YYYY-MM-DD]` and keep the original text.
- `[REVIEWED YYYY-MM-DD: <pointer>]` means the code review loop passed (see `REVIEW_ORCHESTRATOR.md`) and the item is pending the user's in-game test. It replaces `[IN PROGRESS]`.
- **Use one status prefix per entry; never stack them.** [`STATUS.md`](STATUS.md) holds the full per-item state (open / in-progress / reviewed / needs-rework / done), the code location, and the review date, rounds and code hash. Update its row and the TODO prefix in the same change. If the code of a `reviewed` item changes, set it back to in-progress.
- Don't rewrite or reorder the user's entries. Only change the status prefix.
- If an entry is a question rather than a code task (Bug #4 asks how to collect logs), answer the user directly. The README's log section covers the common case.

## 2. Where each change goes

- **Bugs:** all go into the single mod `SupermarketFixes/`. Add one file per bug in `src/Patches/` (for example `RackMoveFix.cs` for Bug #1). Put a doc comment at the top explaining the root cause, then the Harmony patch classes. Give each fix its own config entries in a section named after the bug.
- **Improvements:** each gets **its own mod project** in its own top-level folder (for example `SelfCheckoutSpeed/`), because each will be published separately.
  - Give it a short descriptive PascalCase name, a `net6.0` csproj modelled on `SupermarketFixes/SupermarketFixes.csproj` (same references, the `DeployToGame` target, `EnableDefaultCompileItems=false` with `src/**`), and its own `Plugin.cs`.
  - Add it to the mods table in the root README.
  - Don't make improvement mods depend on SupermarketFixes or on each other.
- **Plugin GUIDs:** `oscar.<modname-lowercase>` (for example `oscar.supermarketfixes`). Use semantic versioning, kept in sync between `Plugin.Version` and the csproj `<Version>`.
- **Every mod has two docs in its folder**, including `SupermarketFixes/`. Keep both current in the same change whenever you alter behaviour, settings, patches or multiplayer requirements:
  - **`README.md` is about the mod, for players and publishing:** what it does, a settings table, whether the host, guest or both need it, and compatibility notes. For SupermarketFixes, one subsection per bug. No implementation details.
  - **`CLAUDE.md` is for agents working on the mod:** current status and open questions, the game mechanism involved, which methods are patched and why, the multiplayer reasoning, and any pitfalls or follow-ups. The full root-cause analysis stays in the doc comment at the top of the patch file; the mod's CLAUDE.md summarises it and points there. Create it when you create the mod.
  - **When you work on a mod, read both first**, so you don't have to re-derive it from the dumps.
  - Per-mod information never goes in this root file.

## 2a. Keep the shared knowledge up to date

Reverse engineering is the expensive part of every task. When you learn something about the game, the tools or the environment that **most future sessions would otherwise have to look up again**, add it to this file before you finish: section 8 for game internals, section 5 for interop pitfalls, section 4 for tools, section 3 for the environment.

Typical candidates are core systems (player, input, singletons, networking patterns), how to read the dumps, and interop techniques. Keep each entry short and only include facts you've verified in the dumps or in-game. If it hasn't been confirmed in-game yet, say so.

Knowledge about one mod or one code path doesn't belong here; it goes in that mod's `CLAUDE.md` (implementation) or `README.md` (player-facing), and in the patch doc comment. If a script would save the next session work, put it in `tools/` and document it in section 4.

## 2b. Independent review after every fix or improvement

Once you have finished implementing a fix or an improvement (it builds, and the mod's README, its CLAUDE.md and the TODO.md are updated), **launch a review subagent** with the Agent tool before you report back to the user.

- **Brief it with the intent only.** Describe *what the change was meant to achieve*: the bug symptom or the feature, and the expected behaviour (including multiplayer expectations, if relevant). Name the mod or folder it lives in so the reviewer knows where to look.
- **Don't include implementation details or your reasoning.** No root-cause theory, no explanation of why you chose this approach, no list of the methods you patched, no "I think this is fine because...". The reviewer must form its own opinion from the code, not inherit your assumptions.
- **Don't steer the verdict.** Don't say what to focus on or what to ignore, and don't hint that the change is safe or risky. Just ask for a review of whether the change correctly and safely achieves that intent, and for concrete findings ranked by severity.
- **Act on the result.** Read the review critically, then apply the changes that are justified. If you disagree with a finding, say so in your final report to the user with your reason; don't silently drop it. Rebuild after applying changes.
- Mention in your final report that the review happened and what changed because of it.

## 2c. Commit often

The repo is a git repo; commit as you go so every step is recoverable and the history shows what changed and why.

- **Commit at each stable point:** a fix or improvement that builds, a review round applied, a docs or `TODO.md`/`STATUS.md` update, a new tool. Don't batch unrelated work into one commit.
- **One logical change per commit.** Keep a mod's code and its README/CLAUDE.md update in the same commit; keep changes to different mods in separate commits.
- **Commit messages are a brief title only:** one short imperative line (for example `Fix rack move desync when guest joins`). No body, no trailers or attribution lines, and no type tags or prefixes such as `feat:`, `fix:` or `chore:`.
- **Stage explicitly** (`git add <paths>`), never `git add -A` blindly. Don't commit `bin/`, `obj/`, `decompiled/` (all gitignored) or log files such as `bug-1/`.
- Commit before starting a risky refactor and after the review loop (section 2b) applies its changes, so a bad change can be reverted cleanly.
- Don't push, amend or rewrite history unless the user asks.

## 3. Environment

| Item | Value |
|---|---|
| Game folder | `~/.local/share/Steam/steamapps/common/Supermarket Simulator` |
| Steam app id / Proton prefix | 2670630 / `~/.local/share/Steam/steamapps/compatdata/2670630/pfx` |
| Engine | Unity 6000.3.6f1, IL2CPP, metadata v39 (`global-metadata.dat`) |
| Loader | BepInEx 6.0.0-be.785 Unity.IL2CPP, .NET 6 runtime, HarmonyX, Il2CppInterop |
| Interop assemblies | `<game>/BepInEx/interop/*.dll` (generated by BepInEx; regenerated when the game updates) |
| Logs | `<game>/BepInEx/LogOutput.log`, `<game>/BepInEx/ErrorLog.log` (fatal/native crashes), Unity `Player.log` (paths in README) |
| Plugin configs | `<game>/BepInEx/config/*.cfg` |
| SDK | .NET SDK 10 (system), `ilspycmd` global tool at `~/.dotnet/tools/ilspycmd` |
| Shell quirks | `cd <dir>` prints a directory listing (a zsh hook). In `native-all/`, with thousands of files, that looks like grep matched everything, so use absolute paths or `cd dir >/dev/null`. `rtk` rewrites `grep`/`ls` output; use `/usr/bin/grep` when exact output matters. |

**Multiplayer setup used for testing:** this Linux PC is the **host**, running the Windows build via Proton with all mods installed. The **guest** is a Mac or Windows PC on the **native build**. Since 2026-11-14 the Windows guest also runs BepInEx (6.0.0-be.793) with some of the mods; its BepInExConfigManager fails to load (UniverseLib missing) and fills LogOutput.log with `FileNotFoundException` spam, which is harmless.
- When the user sends logs, check whose they are: `CLIENT` vs `MASTER` in `[BoxDiag]` lines, and the install path in Player.log. The host's own Unity log is local (`<prefix>/drive_c/users/steamuser/AppData/LocalLow/Nokta Games/Supermarket Simulator/Player.log`, plus `Player-prev.log`). Its `[BoxDiag] t=...` lines give the host's view of the same session; correlate by box uid/view id, since the two clocks differ.
- The host is the Photon master. It runs the employee and customer AI and broadcasts state; guests apply RPCs and run different code paths (for example, rebuilding racks from received data).
- Find out *where* a bug happens before deciding where the fix must run. Prefer host-side fixes when possible: guests may not have mods, and BepInEx IL2CPP support on macOS, especially Apple Silicon, is uncertain and unverified. If a fix must run on the guest, say so clearly to the user and in the mod's README.
- If the host and guest run different mod sets, a mod that changes networked behaviour can desync them. Think about what the unmodded side sees.

**Other mods installed on the host:**
- Plugins: AutoStock, MoreEmployees, CustomerBasket2x, NoMarketDoors, StoreCCTV, OrderHistory, PortableComputerMod, WholeTruckHighlight, BepInExConfigManager, ModSettingsTool.
- `AutoRestockMod.zip` is present but unextracted, so BepInEx doesn't load it.

If a symptom could come from another mod, check whether that mod touches the same classes (`strings <dll> | grep <TypeName>`), and ask the user to test without it.

**You can't run or see the game.** The user tests in-game. Every change ends with clear test steps and with which log lines to send back.

## 4. Reverse-engineering workflow

The game's C# exists only as native code. Two complementary views:

1. **Signatures:** `decompiled/Assembly-CSharp/<Type>.cs`. These are interop stubs (from ilspycmd) with every class, field and method, but the method bodies are just native-call thunks. Use them to find types and members, and as the API your mod compiles against. The `[CallerCount(n)]` attribute hints at inlining; see the pitfalls below.
2. **Logic:** `decompiled/native-all/<Type>.txt` (and `decompiled/native/` for older targeted dumps). Each file has the field offsets at the top, then per method the x86 disassembly and **ISIL**: Cpp2IL's instruction-set-independent IL, in which calls resolve to method names.
   - Read field accesses as `[reg+offset]` and map them using the offset table, e.g. `[rcx+56]` on a RackSlot is `0x38 m_Boxes`.
   - Compiler-generated lambdas, closures and coroutines are separate files, such as `RackSlot+<>c__DisplayClass68_0.txt` and `Rack+<<Place>g__Delay|56_0>d.txt`.

Tools (all in `tools/`):

```sh
# Regenerate dumps (after a game update, or for types outside Assembly-CSharp):
~/.dotnet/tools/ilspycmd -p -o decompiled/Assembly-CSharp "<game>/BepInEx/interop/Assembly-CSharp.dll"
DUMP_ASSEMBLY=Assembly-CSharp dotnet tools/Il2CppDump/bin/Release/net6.0/Il2CppDump.dll decompiled/native-all '*'   # ~10 s
dotnet tools/Il2CppDump/bin/Release/net6.0/Il2CppDump.dll <outDir> <TypeFullName> [more...]   # other assemblies; nested types included
# (rebuild the tool with `dotnet build -c Release` in tools/Il2CppDump if bin/ is missing)

tools/isil.sh decompiled/native-all/Rack.txt "Rack::Place"         # one method's ISIL, prologue/noise stripped
tools/callers.sh decompiled/native-all RackSlot.AddBox              # every method that calls it (constructors: Type..ctor)
tools/modhash.sh SupermarketFixes/src/Patches/X.cs   # short hash of a mod's code (dir: src/**.cs + csproj), for STATUS.md stale-review checks
tools/readconst.py 0x1838CEBA8 [--type f|d|i|q]                     # value of a literal ISIL shows only as [0x18...] (default float)
tools/bindings.py escape                                            # default input bindings: map/action -> key (filter optional)
```

**Game assets** (textures, sprites, names) aren't in the dumps. Inspect them with UnityPy in a throwaway venv (`python3 -m venv <scratch>/upy && <scratch>/upy/bin/pip install UnityPy`), loading `<game>/Supermarket Simulator_Data/sharedassets*.assets` / `resources.assets` and saving `obj.read().image` as PNG. `strings -n 5 <file> | grep -i <name>` is a quick first check. The Addressables bundles in `StreamingAssets/aa` hold only localization. Furniture shop icons are renders in one atlas, `T_IconAtlas_Furnitures` (sharedassets2); it has no computer. Flat white UI glyphs are named `icon_<set>_<n>` (e.g. `icon_device_63` is a computer).

`Il2CppDump` uses the Cpp2IL that ships in `<game>/BepInEx/core`, which already supports this metadata version. Don't download other Cpp2IL or Il2CppDumper builds unless that one stops working.

Typical loop for a task:
1. Find the relevant types: `ls decompiled/Assembly-CSharp | grep -i <keyword>`.
2. Read the native logic with `isil.sh`, and map the flow with a call-graph summary or `callers.sh`.
3. Write down the root cause in a doc comment before patching.
4. Patch, build, and give the user test steps plus diagnostics to report.

## 5. IL2CPP / BepInEx pitfalls (learned the hard way)

- **No transpilers.** Harmony patches are native detours, so only prefix, postfix and finalizer work. To change logic in the middle of a method, either replace the whole method (a prefix returning `false` plus your own reimplementation) or patch around it: fix the state before or after, or patch the methods it calls.
- **Inlining.** Small methods (getters, tiny helpers) may be inlined into their callers, so patching them won't affect every call site. Check the callers' ISIL; if the call isn't there, patch the caller. Identical functions can also share one address; the same `RVA=` on several methods in a dump means patching one patches all of them.
- **Don't write game static fields blindly.** `BoxDiag.Enabled` is a C# `const`. Il2CppInterop still generates a setter for it, and writing it crashed the game (AccessViolation in `il2cpp_field_static_set_value`, because the class has no static storage). Before touching a static, confirm native code actually reads it (`grep -l "typeof(<Class>)" decompiled/native-all/*.txt`). Never touch game state from `BasePlugin.Load()`; the game isn't running yet.
- **An exception escaping a prefix skips the original method.** Wrap patch bodies in try/catch and fail open (`return true`), so the vanilla behaviour still runs.
- **Overloads:** target them explicitly, e.g. `[HarmonyPatch(typeof(RackSlot), nameof(RackSlot.Initialize), new Type[0])]` versus `new[] { typeof(Box) }`.
- **Interop object rules:**
  - Fields are exposed as properties regardless of access modifiers.
  - Use `==` / `!= null` for Unity "destroyed" checks.
  - Compare identity with `.Pointer`.
  - Game lists are `Il2CppSystem.Collections.Generic.List<T>`; iterate with an index.
  - Use `obj.TryCast<T>()` for downcasts.
  - Use `Object.FindObjectsOfType(Il2CppType.Of<T>())` to find all objects of a type.
- **PUN `RPC(..., RpcTarget.All)`** sends to the others and runs the RPC locally straight away (`ExecuteRpc`, native-pun dump), so host-side state changes are visible right after the call.
- **DOTween** is usable through the `DOTween.dll` interop: `DOTween.PlayingTweens(list)` appends without clearing and returns null when nothing plays; `Tween.timeScale` speeds one tween up. Whether brand-new tweens show up in `PlayingTweens` immediately is unconfirmed (BulkShelfTransfer relies on it).
- **Lazy caches:** some game properties fill their backing field on first access (e.g. `RackSlot.OwnRack` fills `m_Rack`). Read the property, not the raw field.
- **Delegates:** Il2CppInterop delegate types convert implicitly from `System.Action`, e.g. `(TweenCallback)(Action)(() => ...)` with DOTween.
- **Custom MonoBehaviours:** give the class an `IntPtr` constructor and create it with `AddComponent<T>()` from the plugin, which registers it into IL2CPP. Input uses the **new Input System** (`UnityEngine.InputSystem.Keyboard.current`), not `UnityEngine.Input`.
- `GetComponentInParent<T>()` skips inactive objects; pass `true` when that matters.
- **ScriptableObjects** (game content definitions) aren't scene objects. Find them with `Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>())`; `FindObjectsOfType` won't return them. Changes to them last until the game is restarted, so keep the originals if you need to restore them.
- **Compiler-generated nested types** (coroutines `<<Method>g__Name|N_M>d`, closures `<>c__DisplayClassN_M`, lambdas) get obfuscated interop names such as `SelfCheckout.ObjectCompilerGeneratedNPrivateSealed...Ob0`. These names can shift between game updates. The native name appears in the stub's static constructor (`GetIl2CppNestedType(..., "<native name>")`).
  - At runtime, find the type by native name: loop over `typeof(Outer).GetNestedTypes(...)` and compare `Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(Il2CppClassPointerStore.GetNativeClassPointer(t)))`. Then patch `AccessTools.Method(t, "MoveNext")` manually with `Il2CppObjectBase __instance`.
  - Iterator fields `<>2__current` and `<>4__this` are exposed as properties `__2__current` and `__4__this`.
  - A coroutine allocates a new `WaitForSeconds` per `yield`. Changing the yielded `WaitForSeconds.m_Seconds` in a `MoveNext` postfix changes that wait.
  - Confirmed working in-game.
- Harmless startup noise: `TypeLoadException` spam in `LogOutput.log` comes from other mods (UniverseLib / HarmonyX type scanning).

## 6. Plugin conventions

- A `[General] Enabled` config entry is the master switch. Its `SettingChanged` handler unpatches or re-applies everything live, so the mod can be hot-unloaded from a config manager without a restart (see `SupermarketFixes/src/Plugin.cs` for a reference implementation). Reset any static patch state when toggling.
- **Deploy the compiled dll into the root of `BepInEx/plugins/`, not a per-mod subfolder.** The csproj `DeployToGame` target copies to `$(BepInExDir)/plugins`; keep it that way in every mod, and tell players to do the same in the README. A leftover copy in a subfolder would load twice.
- Apply each `[HarmonyPatch]` class individually and log failures, so one broken patch (for example after a game update) doesn't disable the others.
- Give every fix or feature its own config entry, with a description that a player can understand.
- Configurable numbers (e.g. speed multipliers) use `ConfigEntry<float>` with a range. Settings show up in BepInExConfigManager and in Mod Settings Tool (the in-game "Mods" tab) automatically.
  - Mod Settings Tool picks the control by reflection: an `AcceptableValues` property → dropdown; `MinValue`/`MaxValue` properties (i.e. `AcceptableValueRange`) → **slider**; no acceptable values → text field. The user wants typed numbers, not sliders (2026-11-15): use a clamp-only `AcceptableValueBase` subclass without those property names (`SelfCheckoutSpeed/src/ClampedFloat.cs`). BepInEx still clamps typed values through `Clamp`.
- Diagnostics/logging can default to on while a fix is unconfirmed. **Default it to off once the user confirms the fix works.** Prefix log lines with a tag such as `[RackMove]`.
- Don't break vanilla behaviour when a feature is turned off: every patch checks its config entry and falls through to the original.

## 7. Status of existing work

Status lives with each mod (its `CLAUDE.md`, section 2) and in the `TODO`; it is not tracked here. Reviews: whoever implements a change launches a fresh review subagent themselves, per section 2b.

## 8. Game internals reference

Verified in the dumps unless marked otherwise. Addresses and offsets are specific to the current game build; check them again after a game update (section 4 explains how to regenerate the dumps).

**Reading ISIL.** Common runtime helpers that show up as `Call 0x18...`:

| Address | Meaning |
|---|---|
| `0x180490C30` | GC write barrier after storing a reference field; read it as a field assignment |
| `0x180491BB0` | Throws NullReferenceException (the target of null-check jumps) |
| `0x180491BA0` | Throws IndexOutOfRangeException |
| `0x180491900` | Runtime metadata init; ignore |
| `0x1815E45D0` | `NoktaSingleton<T>.Instance`. `rcx` is the generic method info, so work out `T` from how the result is used, or from the interop stub. |
| `0x1815E3E30` | `NoktaSingletonPunCallbacks<T>.Instance` |
| `0x180563290` | A shared empty method (no-op), e.g. a base constructor |

- **Object layout.** `List<T>`: `_items` at +0x10, `_size` at +0x18. Arrays: length at +0x18, elements from +0x20, 8 bytes each for references.
- **Const fields.** These appear in dumps as `static` at offset `0x0`.
- **Virtual calls.** `Move rax, [rdx+N]` then `Call rax` (with `rdx = [obj]`, the class) is a vtable call: slot `(N - 312) / 16`. Slots 0–3 are `System.Object`'s virtuals, then the class's own virtuals in declaration order (base class first). Derived from `IPlacingMode` (376 = `get_AvailablePosition`, 408 = `OverlapBoxes`, 424 = `PlacingMode(bool, Material)`), consistent at three call sites; check the stub's `virtual` members when using it.
- **Float literals.** ISIL shows them as `[0x18...]`; read them with `tools/readconst.py`. Cpp2IL sometimes mislabels a float load as `typeof(T)`; take the address from the raw `movss` line above it.

**Singletons and lookups.**
- Managers derive from `NoktaSingleton<T>` (or `NoktaSingletonPunCallbacks<T>`). Use `NoktaSingleton<X>.Instance` and `.HasInstance` from interop.
- `IDManager` maps content ids to their ScriptableObject definitions: `ProductSO(id)`, `FurnitureSO`, `BoxSO`, `BucketSo`, `FloorSO`, `SectionSO`, `StorageSO`, `ProductLicenseSO`, and the employee SOs (`CashierSO`, `RestockerSO`, `JanitorSO`, `CustomerHelperSO`, `SecurityGuardSO`, `CourierSO`, `BakerSO`, `IceCreamHelperSO`). Purchases and spawns look definitions up through these methods; they aren't inlined at the call sites checked so far.

**Player.**
- `PlayerManager.Instance.LocalPlayer` is the local `PlayerInstance`. Every player, remote ones included, has a `PlayerInstance`, so check `PlayerInstance.IsLocalPlayerInstance` before acting.
- `PlayerInstance` holds the per-player components: `InputActions`, `FirstPersonController`, `PlayerController`, `PlayerObjectHolder`, `FurniturePlacer`, `PlayerInteraction`, and one `*Interaction` component per activity (`BoxInteraction`, `RackInteraction`, `DisplayInteraction`, `FurnitureInteraction`, `CheckoutInteraction`, ...).
- **`PlayerInteraction`** turns input callbacks (`OnUse(context)`, `OnCancel`, `OnBack`, `OnClose`, ...) into delegates: `onUse(bool down)`, `onCancel`, `onBack`, `onClose`, `onRotate(bool)`, `onDrop(bool)`, and others. Only the active `*Interaction` component is enabled and subscribed, so invoking `onUse(true)` acts like a Use click for whatever the player is doing.
- `PlayerInteraction.m_InInteraction` is true during an interaction such as holding furniture. `EscapeMenuManager.Pause()` does nothing while it's set.
- `PlayerManager.PlayerInstances` lists all players, local included; `RemotePlayerInstaces` (sic) lists the remote ones. A player's Photon owner is `PlayerInstance.m_PhotonView.Owner`.
- **Movement (`FirstPersonController`, StarterAssets-style):**
  - `Move()` only runs for the current local instance, and returns at once if `_movementInteraction` is false. That flag is false in UIs, at the computer and checkout, and when paused.
  - Target speed is `SprintSpeed` while sprint is held, otherwise `MoveSpeed`. Both are plain fields read every call.
  - `Jump()` is called through the `InputActions.onJump` delegate, never directly.
  - The FPS camera target is `CinemachineCameraTarget`. `Start` saves its localPosition into `defaultCameraPosition`. The only native code that writes its position is `PushCameraEffect` (DOTween `DOLocalMove`); `CameraRotation` only sets its rotation.
  - No game code reads or writes `CharacterController.height/center` for players.
- Placement state: `FurniturePlacer.m_PlacingMode` (furniture) and `PlayerObjectHolder.m_PlacingMode` (objects). `PlayerInteraction.InSkateboard` is set while riding a skateboard.
- **Player models:** `PlayerAnimation` (in the `PlayerInstance` hierarchy) holds `m_Animator` and Animation Rigging rigs (`m_LookAtRig`; hand and leg IK in `PlayerIKHandler`). The locomotion blend tree is fed from `CharacterController.velocity`. There is no crouch state. Not yet confirmed in-game: whether the Animator is humanoid (PlayerCrouch logs it).

**Input.**
- The game uses the new Input System with one asset, `CustomInputActions`. Gameplay actions are in the `Player` map: `Use`, `Take`, `Drop`, `Cancel`, `Back`, `Throw`, `Close`, `Scroll`, `Sprint`, `Jump`, `SnapPlace`, and others. List them with `tools/bindings.py`.
- Players can rebind keys. Look actions up per player, `player.InputActions.m_PlayerInput.actions.FindAction("<name>", false)`, then call `WasPressedThisFrame()` and similar.
- Ctrl is bound to `SnapPlace` (snap while placing). `Sprint` is Shift / left stick press, and `Ping` is right stick press.
- Escape is bound to both `Cancel` and `Back`. On a gamepad, `Back` is B and `Cancel` is Start.
- The `InputActionID` enum (`USE`, `CANCEL`, `CLOSE`, `SCROLL`, ...) is a separate id used for key hints, not for input.

**Key hints (the key list on the side of the screen).**
- `NoktaSingleton<InteractionHintsManager>.Instance.AddHint(InputActionID id, string actionName, string localizationKey, PlayerInstance player)`.
  - `actionName` is an input action name and selects the key icon.
  - `localizationKey` is an entry in the hint string table, such as `_place`, `_rotate`, `_boxUp`, `_cancel`, `_back`, `_take`, `_drop` or `_throw`. List them all with `grep -h -B6 "Call InteractionHintsManager.AddHint" decompiled/native-all/*.txt | grep -o '"_[^"]*"' | sort -u`.
- Hints live in `Hints` (Dictionary keyed by `(int)InputActionID`), so there's **one hint per id**. `RemoveHint(id, player)` removes one; `Clear(player)` empties the dictionary, which interactions do when they start and end.

**Pricing** (`PriceManager`, a `NoktaSingletonPunCallbacks`; per-product data is `Pricing {ProductID, Price, LastChangeDate, DiscountRate, _saleIndex}`).
- Lists: `m_CurrentCosts` (cost per unit), `m_PricesSetByPlayer` (selling prices; `GetPriceSetByPlayer(id)` / `GetOrCreatePriceSetByPlayer(id)`, which creates one at market price), `m_AveragePrices`, `m_DailyPriceChanges`, `m_PreviousPrices`, `pricingDatas`. They're saved in `SaveManager.PriceContainer`, along with the bulk tab's `BulkMarkupRatio` / `BulkDiscountRatio`.
- `Pricing.MarketPrice = round(CurrentCost * (1 + ProductSO.OptimumProfitRate/100), 2)`. `DiscountRate` is a percentage of `Price`.
- With no player price set, the selling price falls back to `CurrentCost`.
- Daily cost changes run on the host or offline only (`PriceManager.Start` subscribes the handler only when `!IsConnectedAndSlave`): `DayCycleManager.OnStartedNewDay` → `TryChangingBasePrices` → `ChangeCurrentCost(id)`, then `BasePricesChanged_Broadcast` sends all lists to guests. Bulk tab: `ApplyBulkMarkup` / `ApplyBulkDiscount` → `FinishBulkPricingApply` (UI refresh, `onPriceSet`, `BulkPricingApplied_RPC`). A single typed price: `PriceSet(pricing)` → RPC `SetPricingList`.
- Active (licensed) product ids: `ProductLicenseManager.m_ActiveProducts`.

**Restockers (employee AI).**
- Restockers are `SupermarketSimulator.Clerk.Clerk` (`RestockerSO.RestockerPrefab`). The old `Restocker` class is unused.
- `Clerk.ManualUpdate` ticks a state machine only when `!PhotonNetwork.IsConnectedAndSlave` (host/offline). The states are `ClerkIdleState`, `ClerkRestockingState`, `RackingState` and `ClerkDropEmptyBoxState`.
- `ClerkIdleState.OnTick` calls `SearchHandler.PerformSearch` every 3 s, plus once on the first tick in Idle. The search fills `BoxTarget` (`IBoxTarget`: `RackSlotBoxTarget`, `StreetBoxTarget`, `VehicleBoxTarget`, or `CarryBoxTarget` for the box in hand), `DisplayTargets` and `RackTarget`.
  - Transitions: `DisplayTargets.Count > 0` → Restocking, `RackTarget` → Racking, back to Idle when the state's `Sequence` completes.
  - A clerk already carrying a box only searches for work for that box.
- States run a `SupermarketSimulator.Sequence.Sequence` of `SequenceTask`s, kept in the list `m_Tasks` and read at every step. Restocking is `[MoveTo, TakeBox, RestockAllDisplays, GoToTrash]`. `TaskStatus`: Running, Fail (ends the sequence), Success (next task).
- `Clerk.CarryBox(box)` sends `ClerkCarryBox_RPC` to guests, then calls `CarryBox_Internal`. That sets `m_CurrentBox`, parents the box to `m_BoxHolder` and tweens it to local zero. `CarryBox(null)` empties the hands without un-parenting anything.
- Level: `NoktaSingleton<EmployeeLevelManager>.Instance.TryGet(clerk.LevelKind, clerk.LevelEmployeeId, out progress)`, then `progress.Level`.
- Interop: `RackSlotBoxTarget` and the other targets don't derive from the `IBoxTarget` interop class. Wrap them with `new IBoxTarget(obj.Pointer)`.
- Not yet confirmed in-game (RestockerDoubleCarry is untested).

**Interaction dispatch** (`PlayerInteraction`).
- `Raycast()` hits colliders in `m_InteractableLayer`. It takes the `IInteractable` from the **hit collider's own GameObject** (`TryGetComponent`, no parent search) and stores it in `m_CurrentInteractable`.
- `Interact(hold, custom)` calls `InstantInteract()` or `HoldingInteract()` on it.
  - If that returns true, it sets `m_InInteraction` and calls `SetCurrentInteraction(m_CurrentInteractable.Type)`, which enables the `*Interaction` component of that `InteractactableType` and hands it the interactable. (`ComputerInteraction.set_Interactable` casts to `Computer`.)
  - Then it calls `SetCurrentInteractable(null)`.
- Hints come from the interactable's `InteractionType` (INSTANT/HOLD/BOTH...) and `InstantInteractionHint` / `HoldingInteractionHint` keys.
- Plain `Furniture` is HOLD only (BOTH with a `SecurityCamera`). Its `HoldingInteract` starts moving it.

**Furniture pipeline** (verified in dumps; used by ExtraComputers, not yet confirmed in-game).
- A furniture type is a `FurnitureSO` {`ID`, `BoxSize`, `FurnitureIcon` (shop sprite), `AtlasIndex`/`AtlasPosition` (box label), `FurniturePrefab`, `FurnitureColor`, `IsMainFurniture`, `Cost`} in `IDManager.m_Furnitures`. `IDManager.FurnitureSO(id)` is a FirstOrDefault over it, used by the cart, the delivery, `FurnitureBox`, box-up and the spawner.
- Shop: `FurnituresViewer.SpawnFurnitures` lists the SOs with `IsMainFurniture`, skipping id 7000. Names come from `LocalizationManager.LocalizedFurnitureName(id)`. Cart totals multiply by `Cost`.
- Box label: `ProductAtlasManager.SetFurnitureIcon(id, renderer)` sets `sharedMaterial = atlasedMaterials[AtlasIndex]`, then a `_BaseMap` texture offset.
- Spawning: `FurnitureGenerator.SpawnFurniture`, with two overloads: (id, pos, boxViewId) for unboxing and (id, pos, rot, parent) for loading a save.
  - Offline: `LeanPool.Spawn(prefab)`.
  - Online (master only): it branches on the prefab's component type to `NetworkObjectGenerator.SpawnNetwork<Type>`, which calls `PhotonNetwork.InstantiateRoomObject("Furnitures/<Type>/" + prefab.name, pos, rot, 0, {boxViewId})`.
  - Photon uses the stock `DefaultPool`: `ResourceCache` first, then `Resources.Load`. Adding a GameObject to `((DefaultPool)PhotonNetwork.PrefabPool).ResourceCache[id]` makes any prefab network-instantiable.
  - `NetworkFurniture.OnPhotonInstantiate` reads `data[0]` (the box interaction's view id), transfers ownership to that player and calls `FurnitureBoxInteraction.OpenBoxOrder` to start placing.
- There are **no `Furniture` subclasses**: every furniture prefab uses the plain `Furniture` class plus type scripts (`Display`, `Rack`, `Trash`, ...), and every type in the shop has its own network spawner. A mod can't count on finding a "plain" furniture prefab to copy (ExtraComputers' donor search found none in-game).
- `Furniture.m_ID` is serialized in the prefab; nothing sets it at runtime. Saves are `FurnitureManager.m_FurnituresDatas`. `LoadFurnitureDatas` logs "Can't spawn furniture with id" for unknown ids and continues.
- Moving/placing uses `FurniturePlacingMode` (base `IPlacingMode`).
  - Serialized: placing tags, `m_PlacingModeCollider` (overlap box), `m_Renderers` (hologram), `m_OwnerCollider`, `m_PhysicalColliders`, `m_NavMeshObstacles`, `m_Furniture` (the transform that gets moved), `m_ParentPhotonView`.
  - `Awake` recomputes `m_AllColliders` and `m_Objects` (`NavmeshBuildSourceObject`) from `m_Furniture`.

**Store computer** (`__Project__.Scripts.Computer.Computer`; one per scene, and several managers hold a reference to it, so it can't be cloned).
- It isn't a `Furniture`, but it has its own `m_PlacingMode` and can be moved.
- `InstantInteract`:
  - refuses if `IsOccupied` (another player is on it);
  - otherwise `set_OpenPC(true)` activates `m_PCCam`, a camera that looks at the world-space canvases under `m_ComputerScreen`;
  - sets `OccupiedPlayer` and, online, `NetworkComputer.InteractComputer_Broadcast(true)` (receivers only set `IsOccupied` / `OccupiedPlayer`);
  - puts the avatar in front of the cam.
- `Back()` (called from `ComputerInteraction.OnCancel` / `GamepadBack`) does `set_OpenPC(false)` (which broadcasts false), then puts the player at `m_PlayerPosition`.

**Holding objects (`PlayerObjectHolder`).**
- Every holdable (Box, FurnitureBox, PaintBucket, FloorBox, OrderPaperBag, IceCreamBucket) goes through `HoldObject(item, posOffset, rotOffset)`. It makes the colliders of `item`'s active objects triggers, the root Rigidbody kinematic, saves `item.layer` in `m_CurrentObjectsLayer` and sets "Ignore Raycast", takes the item's `IPlacingMode` as `m_CurrentPlacingMode`, stores `m_CurrentObject` (+0x48), and parents the item **directly** to `m_ObjectHolder` (+0x20), tweening it to the offset.
- It **overwrites** `m_CurrentObject` and the saved layer without releasing the previous object. `DropObject` / `TryDropObject` / throw / `ReleaseObject` only release the current one; `DropObject` doesn't clear `m_CurrentObject`.
- `DropObject`: unparent, `DOKill`, restore the saved layer, `m_CurrentPlacingMode.PlacingMode(false, null)` (hologram off, triggers cleared), colliders solid, physics on. `NetworkRigidbodySetter` keeps it kinematic online if another client owns its PhotonView.
- Vanilla drops a box only through placing mode (`BoxInteraction.OnDrop` → `StartPlacingMode`). `IPlacingMode.AvailablePosition` is just "`m_Triggers` is empty", and only `OverlapBoxes` (from `FixedUpdate`, while placing) fills `m_Triggers`, so `TryDropObject` accepts any spot outside placing mode. `OverlapBoxes` tests the layers Default, Interactable, QueryOnly, WallLayer, Ignore Raycast.
- Networked box drop: `NetworkBoxInteraction.DropBox_Broadcast()` uses `BoxInteraction.m_Box`: it sets the box's `PhotonTransformViewOptimized.m_SyncEnabled` and clears `NetworkBox.IsNetworkOccupied` locally, then `DropBox_RPC` (Others) does the same on each receiver's copy and calls `BoxInteraction.DropBox(userId)` → that player's `DropObject` (current object only), `SetOccupy(false)`, `InteractionEnd`. `IsNetworkOccupied` is set only by the host's `PickUp_Broadcast`, and the host rejects pickups while it's set.
- `NetworkBox.DisableNetworkTransformSync(bool isActive)` sets `m_SyncEnabled = isActive`, despite the name: `false` turns sync off.

**Boxes in hand (`BoxInteraction`).**
- Offsets: `m_Box` +0x48, `m_PlacingMode` +0x50, `m_holdingUse` +0x51, `m_IsBoxOpen` +0x52, `m_holdingTake` +0x58, `m_CurrentDisplaySlot` +0x60, `IsWaitingAvailability` +0x118.
- `BoxInteraction.Update` places products only while `m_holdingUse`, `Box.IsOpen`, `!m_holdingTake`, `m_IsBoxOpen` and `Box.HasProducts` all hold, and once per `m_ProductInteractionInterval`.
- `m_IsBoxOpen` is BoxInteraction's own copy of "open", separate from `Box.IsOpen` (`BoxData.IsOpen`). It is set when the box is bound, cleared by `OnClose` (C), and set by a delayed call after an open click.
- `Box.OpenBox`/`CloseBox(false)` are ignored for 0.7 s after every open or close (`m_InAnimationTransition`). Online they go through `NetworkBox.OpenBox_Broadcast`/`CloseBox_Broadcast`.
- Display slot checks (`CheckPlaceProductToSlot`): the display type must match; refused only if the slot has products of another id, or is full. Labels aren't checked, and `DisplaySlot.AddProduct` relabels an empty slot. `DisplaySlot.HasProduct` = `ProductCount > 0`. `ItemQuantity.HasLabel` = `FirstItemID > 0`.
- Online place: guest → `NetworkBoxInteraction.AskPlaceProduct` → the host re-checks on its copy of that player's BoxInteraction (`TryPlaceProductToDisplay(slot, pos)`) → `PlaceProduct_RPC` to all (it opens the box if closed).
- A client applies `TakeProduct_RPC` / `PlaceProduct_RPC` only if the message's box is its own `BoxInteraction.m_Box` (else it logs "Boxes are not same" and skips it), and `DropBox` clears `m_Box` at once. A box that leaves the hands while asks are in flight desyncs.
- Online take is **not** checked: guest `AskTakeProduct` → host `AskTakeProduct_RPC` relays `TakeProduct_RPC` (All) as-is, and `Box.AddProduct` has no capacity check. A mod that sends takes from a guest must cap them itself.
- Capacities: a box holds `ProductSO.GridLayoutInBox.productCount` (`Box.Full` / `MaxProductCount`); a shelf slot holds `ProductSO.GridLayoutInStorage.productCount` (`DisplaySlot.Full`, virtual, overridden by ice cream slots).
- Product fly animation: `Box.AddProduct` / `DisplaySlot.AddProduct` tween with `DOLocalJump` + `DOLocalRotateQuaternion` (0.3 s) and `DOScale` (0.2 s).
- `BoxInteraction.OnUse(true)` opens the held box only when `m_Box` is set, it isn't waiting for the host, nothing is targeted (`m_TargetPlacementArea`, `m_CurrentRackSlot` unless `m_InAnimationTransition`, `m_CurrentTrashBin`, `m_CurrentPlacementArea`), it isn't in placing mode, and the box is closed. On an **open** box it just sets `m_holdingUse`. Nothing closes a box on drop or on pickup (the `CloseBox` in `Box.PickUp`/`InstantInteract` is guarded by `m_Racked`), and `set_Interactable` copies `Box.IsOpen` into `m_IsBoxOpen`.
- Pickup: `Box.PickUp()` (offline, no args) doesn't bind the box controls; `PlayerInteraction.Interact` → `SetCurrentInteraction(BOX)` does that afterwards. `Box.PickUp(PlayerInstance)` calls `BoxInteraction.set_Interactable` itself. `NetworkBox.PickUP_RPC(userId)` (no sender or occupancy check) makes every client run `EndAvailabilityWait` + `Box.PickUp(player)`. A refused or timed-out guest pickup calls `BoxInteraction.CancelInteracitonForOnlyMultiplayer` (the timeout sets `NetworkBoxInteraction.m_AvailabilityTimedOut` first). `PlayerInstance.OnPlayerLeftRoom` (host) only throws out the leaving player's `BoxInteraction.m_Box`; anything else parented to that avatar is destroyed with it.
- `BoxInteraction.OnTake` only sets `m_holdingTake`. Pickup wait: the 3 s timeout sets `m_AvailabilityTimedOut`, clears `IsWaitingAvailability` and ends the interaction; the next `EndAvailabilityWait` clears that flag and returns, so a late refusal ends nothing and a late `PickUP_RPC` still calls `Box.PickUp` (a ghost-item source). Vanilla ignores drop, throw and use while `IsWaitingAvailability` is set.
- `BoxInteraction.UpdateOpeningBoxHint` re-adds the USE `_open` hint for a closed box; `RackHints` calls it and then adds its own USE `_place`.
- Other containers: online-order paper bags (`OnlineOrderInteraction`) apply moves on the acting client and broadcast them; taking goes through `OnlineOrderService.TryTakeProductIntoBag` (returns `TakeProductStatus`). `CrateInteraction` has the names swapped: `OnTake` (right click) sets `m_IsOnUse` and runs `<OnUseLoop>` (shelf → crate); `OnUse` (left click) sets `m_IsOnTake` and runs `<OnTakeLoop>` (crate → shelf). Both yield `WaitForSeconds(TotalProductInteractionInterval)`.

**Racks and racked boxes** (verified in dumps; behaviour on join confirmed by in-game logs).
- `RackSlot`: `m_Boxes` (+0x38, live Box objects) and `m_Data.RackedBoxDatas` (the records that are saved and sent to guests). Racked boxes are drawn through `InstancedRenderer` snapshots (`Box.ToggleInstanced`), not their own mesh.
- **Box UIDs are not unique.** `IDManager.GenerateBoxUniqueID` is a per-session counter (`BoxUniqueIdCount`, not saved; returns -2 on guests), and guests keep the host's UIDs. Match boxes on UID + ProductID (+ position), never UID alone.
- A box placed on a rack during the session stays **networked** (`RackSlot.ENABLE_DEMATERIALIZE_ON_PLACE` is false). Boxes loaded from the save are **local** copies (`SpawnLocalRackedBox` / `BoxGenerator.SpawnBoxInRack`). Taking from a slot (`RackSlot.TakeAndMaterializeBox`, host) either broadcasts `RemoveLocalRackedBox` and `MaterializeRackedBox`s a new networked box, or for a networked box just unracks it. `RemoveLocalRackedBox_RPC` → `RemoveLocalRackedBoxByUID` → `RemoveBox` runs on every client.
- `RackSlot.RemoveBox(box)` is the full, purely local cleanup (m_Boxes, records, instancing, highlight, `InventoryManager.AddBox`, label, `RePositionBoxes`). Only TakeBoxFromRack, RemoveLocalRackedBoxByUID, DematerializeNetworkedBox and clerk pickups call it. `Box.PickUp` (the player's box path) does **not**.
- A guest's copy of a networked box created by `NetworkBox.OnPhotonInstantiate` is unparented and made kinematic; nothing re-racks it.
- `RemoveBox` drops **every** record with the box's UID (`RemoveAll`, compares `BoxData.m_UID`). When no record is left it clears the slot: `RackSlotData.Clear` (records, `BoxID = -1`; `ProductID` kept) and `RackManager.RemoveRackSlot(productId, slot)`. Records are the boxes' own `BoxData` instances; `UpdateRackedBoxDatas` rebuilds them from `m_Boxes`.
- **Dematerialize** (networked racked box → local racked box, host-driven, handled by unmodded clients): `NetworkRack.DematerializeBox_Broadcast(slotIdx, uid)` (RpcTarget.All) → `RackSlot.DematerializeNetworkedBox(uid)`: first `m_Boxes` entry with that UID; only if it is networked and not `m_IsBoxOccupied`: `RemoveBox`, `InventoryManager.RemoveBox`, `DetachDataBeforeDespawn`, master `PhotonNetwork.Destroy`, `SpawnLocalRackedBox(data)`. Its only game caller (`AddBox`, `ENABLE_DEMATERIALIZE_ON_PLACE`) is compiled out. **Hazard:** on a slot's only box, `RemoveBox` sets `BoxID = -1`, so `SpawnLocalRackedBox` gets `IDManager.BoxSO(-1)` = null and returns null: the box is lost on every client. Not yet confirmed in-game (SupermarketFixes uses it, opt-in).
- Destroying or disabling a racked box cleans up its instanced snapshot (`Box.OnDisable` → `SubRenderer.Unregister`, `Box.OnDestroy` → `RemoveInstance`). Only a box that stays alive while outside `m_Boxes` leaves a ghost.
- A player placing a box on a rack (`NetworkBoxInteraction.PlaceBoxToRack_RPC`) transfers the box's PhotonView back to the master client (asynchronously). Clerk rack targets are `RackSlotBoxTarget(slot)` and hold no box reference.

**Multiplayer patterns.**
- Host-only branches show in ISIL as calls to `PhotonNetwork.get_IsMasterClient` or `get_IsConnectedAndMaster`. Guest-only branches call `get_IsConnectedAndSlave`. Grep a method's ISIL for these to find where its logic runs.
- `Network*` companion classes use a naming convention:
  - `X_Request`: guest asks the host;
  - `X_Broadcast`: the sender calls `photonView.RPC("X_RPC", ...)`;
  - `X_RPC`: received on the other clients;
  - `X_Order` / `X_Response`: applies the result.
- Placing moved furniture: the place RPCs of Furniture, Rack, Checkout, CategorySign, Scale, IceCream stand and Computer carry **no position**. Other clients place their copy at whatever transform the Photon transform view has streamed so far. Display, Vending, StorageLight and StoreLight send pos/rot. Anything that teleports furniture should push the exact pose first: `PhotonTransformViewOptimized.BroadcastCurrentTransform()` sends the game's reliable `SyncTransform` RPC. Receivers `SetPositionAndRotation` and reset their interpolation target. It only sends if `photonView.IsMine`. Reliable RPCs are ordered, so it lands before a place RPC sent right after. Not yet confirmed in multiplayer.
- **Per-player synced data:** Photon player custom properties. The game itself sets `sid` and `png` on `PhotonNetwork.LocalPlayer`. A mod can add its own key with `SetCustomProperties(new ExitGames.Client.Photon.Hashtable { [key] = value })`; `Il2CppSystem.Object` converts implicitly from string and bool. Unmodded clients just store the key, the value persists for late joiners, and others read it from `owner.CustomProperties`. Used by PlayerCrouch; not yet confirmed in-game.
- Networked objects (boxes, furniture boxes, paint cans) are spawned by the master client (`BoxGenerator.SpawnBox` branches on `IsMasterClient`). Their data reaches guests through Photon instantiation data, so changing spawn data on the host affects everyone.
