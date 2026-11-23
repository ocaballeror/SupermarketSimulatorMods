/*
 * EmployeeClothes: pick the color of the employees' clothes from the computer (Improvement #15).
 *
 * HOW THE GAME BUILDS EMPLOYEES (verified in the shipped assets with UnityPy, 2026-11-17; the recoloring
 * and the UI have been through several in-game rounds, see CLAUDE.md for what is still unconfirmed)
 *  - Every employee type has several prefabs (Restocker, Restocker_02..06, Cashier, Woman_Cashier, CustomerHelper,
 *    SecurityGuard, Janitor, Courier, Baker, IceCreamHelper_01). The scene-asset prefab is reached through the
 *    employee ScriptableObjects (IDManager.m_Cashiers / m_Restockers / m_CustomerHelpers / m_SecurityGuards /
 *    m_Janitor / m_Bakers / m_IceCreamHelpers / m_Couriers -> *SO.<X>Prefab). The networked copy (Resources, with
 *    PhotonView) is a separate asset; both look the same.
 *  - The look is a skinned Mixamo-style model under "Animated Model" (or similar): SkinnedMeshRenderers LOD0/1/2,
 *    URP/Lit materials with a baked albedo texture (_BaseMap == _MainTex) and _BaseColor = white. There is NO
 *    per-employee customisation at runtime (CharacterCustomize / BmanCustomize ... exist but aren't on employees) and no
 *    tint property to turn. EmployeeTypeColorsSO is only the HR card accent color, not the clothes.
 *  - Clothes are mostly separate materials (shirt, pants, jacket, waistcoat, dress, hat, helmet ...), but names lie and some
 *    materials are atlases. The classifier looks at "material name | base texture name" (Recolor.ClothesRx) and then
 *    refuses anything shoe, boot, hair or "yaka" (Recolor.NotClothesRx): shoes are never recolored, and the materials
 *    called "Hair_Mat" (textures Low_Hair_Tshirt_Mat / Low_Hair_Yaka_Mat) are the hair plus the "RESTOCKER" name badge
 *    (checked by drawing their UV islands over the textures), not a T-shirt. A few clothes materials also hold things
 *    that aren't clothes: BritneyBodyMAT (Restocker_05/06: jeans + hands + face + hair + ears), Low_Dress_Mat (Restocker_03/05:
 *    a blue knit hat + a dark hair blob + the badge), Man_02_Down_Dress_Diff (Baker: jeans + boots + belts), BouncerBody_MAT
 *    (guard: black torso + trousers + shoes + soles + belt), Female_Scientist_TOP (a red emblem). For these only the garment pixels are recolored (per
 *    texture rule: blue hue, jeans islands, the guard's dark suit pixels), see "Masked atlases" below. The full verified table of every
 *    renderer of every employee prefab is in EmployeeClothes/CLAUDE.md.
 *
 * HOW THE CLOTHES ARE RECOLORED (no model, no mesh and no animation is touched)
 *  - The garments' textures already have color (blue shirts, red dresses, black suits), so multiplying _BaseColor would
 *    only darken them. Instead, once per source texture, a "shade map" is made on the CPU: the texture is copied through
 *    a RenderTexture (the originals aren't readable), each pixel's luminance is compared with the texture's mean
 *    luminance, shade = 1 + (L - mean) * 1.4 clamped to 0.45..1.45, and the map stores shade / 1.45 as grey (alpha is
 *    kept). Folds, seams and logos survive as light/dark variation. The map is compressed to DXT5 (about 1 MB each).
 *  - For every (original material, employee type) a material copy is made whose _BaseMap/_MainTex is the shade map and
 *    whose _BaseColor/_Color is chosen color * 1.45 * garment shade. The renderer's sharedMaterials entry is swapped.
 *    Changing the color is then only a SetColor on the copies (so the live preview is free), and "no color" swaps the
 *    original material back. Pants are 0.7x and waistcoats 0.8x of the color (Plugin.ShadedGarments, a constant; so
 *    an outfit isn't one flat color).
 *  - Masked atlases: for the mixed materials above, a working copy of the texture (at most 512x512, through a RenderTexture)
 *    is made once; the rule picks the garment pixels, and the shade of each garment pixel is computed like the shade map
 *    (relative to the garment pixels' mean). For every (material, employee type) a baked RGBA texture is kept: garment
 *    pixels = ShadeV * color * 1.45 * garment shade in sRGB bytes, clamped (what the shade-map material shows; Material.SetColor
 *    converts the color to linear itself), every other pixel = the original.
 *    A new color is one pass over the garment pixels plus SetPixels32/Apply (a few ms), skipped when the color is the same
 *    as last time. Recolor.GetMaterial(original, slot, color) is the API (cached copy per (material, slot)); the time budget
 *    postpones a bake to the next pass, so dragging a color picker never stalls a frame. The baked textures belong to the
 *    material copies and are destroyed with them (after the original materials are put back).
 *  - Employees (Employees.cs, driven by ClothesDriver) are found by events, not by searching: a Harmony postfix on the
 *    Start of every employee class (EmployeeTracker.cs: Cashier, Clerk, CustomerHelper, SecurityGuard, Janitor, Courier,
 *    Baker, IceCreamHelper) registers a hired or loaded employee when it first becomes active; pooled employees (LeanPool
 *    deactivates fired ones and brings them back later) stay known. A pass over the known employees runs only when
 *    something changed (a new employee, a new color, a postponed shade map to retry, or an employee's own 30 s
 *    verification timer), makes the renderers of the model (the top Animator's sub-tree, cached) match the stored color and
 *    re-asserts our materials. Safety nets: a slow FindObjectsByType (including inactive) per colored type, a rolling scan
 *    of all types after a scene start or when the mod is switched on, and a faster scan of any type whose Start hook isn't
 *    applied. That covers employees that exist already, hired later, loaded from a save, spawned by the network on a
 *    guest, and anything that restores the original materials. A texture budget (16-25 ms per pass, always at least one
 *    build) spreads the one-off shade-map creation over a few passes.
 *  - Every swap is recorded in a registry (renderer, slot, original material per index). Restoring (Reset, a type losing
 *    its color, disabling the mod, a new scene) goes through the registry, so it reaches pooled employees too and runs
 *    before any material copy is destroyed; a renderer whose copy was destroyed anyway falls back to the recorded
 *    original. All caches are keyed by GetInstanceID, not by interop pointers.
 *
 * THE UI (clones of existing UI; the screen is a world-space canvas rendered by the PC camera, see ExtraComputers)
 *  - The Customization tab (CustomizationTab > Unlocked > Sections Scroll View > Viewport > Content, a GridLayoutGroup)
 *    holds three cards: Store Name UI (ChangeNameItem), Enterence Position UI, Enterence Apperence UI. A fourth card is
 *    made by instantiating the Store Name card under an inactive holder (no Awake runs), destroying its ChangeNameItem,
 *    the Localize components and the button's listeners, and then re-parenting it. It is injected by a postfix on
 *    CustomizationTab.OnEnable (fallback: a poll in the driver).
 *  - The price ($1000, Plugin.Price) is charged when the card is CLICKED and the panel opens: charge first, and the panel
 *    opens only if the charge worked (a failure after charging refunds it). This is a paid SESSION: inside it, picking
 *    colors, Apply, Apply to all, Reset and switching type are all free. The session ends on Done, on switching to
 *    another tab (the panel is deactivated: PanelWatcher.OnDisable), on leaving the computer (a patch on
 *    Computer.OpenPC = false, plus a Tick check of the PC camera) and on a scene change or disabling the mod. The panel
 *    is only ever activated by a charged click, so it can't be re-entered for free; reopening costs again. The charge is
 *    taken like the game's store-name change (ChangeNameOverlay.ApproveStoreName): offline / host
 *    MoneyManager.MoneyTransition(-price, UPGRADE_COSTS, true) (MoneyManager already broadcasts to guests); a guest
 *    would use NetworkMoneyManager.OnMoneyTransition_MasterRequest (guests can't open the panel, the branch mirrors
 *    the game). The game's feedback for "can't afford" is the card button being disabled while !HasMoney(price)
 *    (ChangeNameItem.UpdatePurchaseButton / CheckRequirements), which the card does too; it shows the price. The colors
 *    are written to the mod's own file when the game saves (see Persistence), so they stay tied to the money.
 *  - The panel (a child of the tab, over its own opaque background Image; the tab's own cards are hidden while it is open
 *    and shown again when the session ends, with a safety net in the CustomizationTab.OnEnable patch) has: a RenderTexture preview, arrows to switch
 *    between the 8 employee types (HrEmployeeKind), the game's own ColorPicker widget (cloned from the store-name color
 *    picker in the NameChangeCanvas; its Image material is reset so it makes its own), 12 swatches (a fallback if the
 *    picker's shader doesn't work), an "Apply to all" switch (a clone of the game's own ToggleSwitch from the HR tab:
 *    a Slider + handle Button + on/off colors; its gamepad glyph and caption are removed and onValueChanged is ours),
 *    Apply, Reset and Done. The switch state is ClothesStore.ApplyToAll (kept across type switches,
 *    Apply, Reset and game starts; stored as the line "#applyall<TAB>1" in flamboyanthopper.employeeclothes.saves.txt, not in the cfg). The type switcher only offers the unlocked types, worked out each time the panel
 *    opens (ClothesStore.UnlockedKinds): the HR tab hides the ice cream helper unless DLCTestController.IceCreamDLC and the
 *    baker unless DLCTestController.BakeryDLC (HiringTab.RefreshAvailability); on top of that a type needs a hired employee
 *    (EmployeeManager.IsEmployeeHired) or a hire slot (HrHiring.SlotIds) with no unmet requirement (HrHiring.MissingCount
 *    == 0: store level, checkouts, ovens ...). Any failure shows the type. Apply to all still writes all 8 types.
 *  - The preview: a camera far below the map (y = -5000, layer 4 "Water": every layer of this game is named, and the
 *    URP renderers don't draw layer 31 "FPSHand", see Preview.cs) renders to a 1024x512 RenderTexture. It sees
 *    clones of the model sub-tree ("Animated Model", found as the top Animator) of every prefab of the selected type, in a
 *    row, standing still facing the camera, lit by two point lights (limited range, so the store isn't lit; URP ignores light layer masks).
 *    The clones use their own material copies (preview slot) so dragging the picker never changes real employees.
 *
 * MULTIPLAYER
 *  - The host runs the employees; guests have their own copies of the same models (Photon instantiates the networked
 *    prefab), and materials are local, so the color must be applied on every client that wants to see it.
 *  - Only the host (or the solo player) can change the colors (card disabled for guests: a guest's choice would need a
 *    custom RPC and a way to charge the host). The host publishes the colors as the Photon ROOM property
 *    "flamboyanthopper.employeeclothes" (a string like "Restocker=ff8800;Cashier=2255aa"). The game itself keeps "Money",
 *    "roomStartTime" and "RackCount" there and every room-property listener only reads its own key, so unmodded clients
 *    ignore ours. Late joiners get it from the room; modded guests poll it once a second and apply it. Unmodded guests
 *    see the original colors (harmless).
 *  - Persistence: the host's colors are saved in BepInEx/config/flamboyanthopper.employeeclothes.saves.txt, one line per save file
 *    ("slot_N.es3"), because the game's own save format can't be extended. Apply and Reset only change the colors in
 *    memory and mark them dirty; the line is written by a postfix on SaveManager.Save(string) (the private leaf that every
 *    save path ends in: autosave, quick save, menu save, new game), under the file name being saved. So the colors follow
 *    the game save exactly like the $1000 does: quitting without saving takes both back (no free apply by reloading).
 *    A new game must not inherit the slot's old colors: a postfix on SaveManager.Clear (reset of the game data: main
 *    menu start, new game, restart after game over) empties the colors and sets "fresh", so the Save(string) that
 *    follows a new game writes "no colors" (removes the line); a postfix on SaveManager.ApplySaveData (loading an
 *    existing save) clears "fresh" again, because the main menu's own Clear comes before a load. The colors of the
 *    previous save are dropped as soon as a scene starts (IDManager.Awake) instead of when the next Tick loads the new
 *    key. The "Apply to all" switch is global and is written at once (it costs nothing).
 *
 * Patches (14, each applied on its own and logged if one fails): CustomizationTab.OnEnable (inject the card, keep the
 * cards visible), IDManager.Awake (a new game scene: forget cached objects), Computer.OpenPC setter (leaving the computer
 * ends the session), SaveManager.Save(string) / Clear / ApplySaveData (commit the colors with the game save) and the eight
 * employee Start postfixes of EmployeeTracker.cs (register new employees). Everything else
 * is public API calls and a few slow polls (ClothesStore.Tick once a second, TabUi.Tick every 0.5 s), so a game update
 * breaks little.
 */
using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace EmployeeClothes;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "flamboyanthopper.employeeclothes";
    public const string Name = "Employee Clothes";
    public const string Version = "1.0.10";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> VerboseLogging;

    // Fixed values (no longer settings).
    internal const float Price = 1000f;
    internal const bool ShadedGarments = true;
    internal const float PreviewLight = 30f;

    /// <summary>True while the patches are applied and the mod does its work.</summary>
    internal static bool Active { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");
        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Enable verbose logging for debugging purposes.");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);

        ClassInjector.RegisterTypeInIl2Cpp<PanelWatcher>();
        AddComponent<ClothesDriver>();
        SetActive(Enabled.Value);
        Log.LogInfo($"{Name} {Version} loaded ({(Active ? "enabled" : "disabled")})");
    }

    private void SetActive(bool on)
    {
        if (on == Active) return;
        Active = on;
        if (on)
        {
            ApplyPatches();
            ClothesStore.OnEnabled();
        }
        else
        {
            try { _harmony.UnpatchSelf(); } catch (Exception e) { Log.LogWarning("Unpatching failed: " + e.Message); }
            Log.LogInfo("[EmployeeClothes] All patches removed (disabled)");
            try { ClothesStore.OnDisabled(); } catch (Exception e) { Log.LogWarning("[EmployeeClothes] Cleanup failed: " + e.Message); }
        }
    }

    private void ApplyPatches()
    {
        // One class per patch; a failing one is logged instead of taking the plugin down.
        int ok = 0, total = 0;
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            total++;
            try
            {
                _harmony.CreateClassProcessor(type).Patch();
                ok++;
            }
            catch (Exception e)
            {
                Log.LogError($"[EmployeeClothes] Failed to apply {type.FullName}: {e}");
            }
        }
        Log.LogInfo($"[EmployeeClothes] Patches applied ({ok}/{total})");
    }

    public override bool Unload()
    {
        if (Active) SetActive(false);
        return true;
    }

    internal static void Info(string msg) => Logger?.LogInfo("[EmployeeClothes] " + msg);
    internal static void Warn(string msg) => Logger?.LogWarning("[EmployeeClothes] " + msg);
    internal static void Error(string msg) => Logger?.LogError("[EmployeeClothes] " + msg);
    internal static void Debug(string msg)
    {
        if (VerboseLogging != null && VerboseLogging.Value) Logger?.LogInfo("[EmployeeClothes] " + msg);
    }
}
