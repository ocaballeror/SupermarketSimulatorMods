/*
 * EmployeeClothes: pick the colour of the employees' clothes from the computer (Improvement #15).
 *
 * HOW THE GAME BUILDS EMPLOYEES (verified in the shipped assets with UnityPy, 2026-11-17; nothing here has been
 * run in-game yet)
 *  - Every employee type has several prefabs (Restocker, Restocker_02..06, Cashier, Woman_Cashier, CustomerHelper,
 *    SecurityGuard, Janitor, Courier, Baker, IceCreamHelper_01). The scene-asset prefab is reached through the
 *    employee ScriptableObjects (IDManager.m_Cashiers / m_Restockers / m_CustomerHelpers / m_SecurityGuards /
 *    m_Janitor / m_Bakers / m_IceCreamHelpers / m_Couriers -> *SO.<X>Prefab). The networked copy (Resources, with
 *    PhotonView) is a separate asset; both look the same.
 *  - The look is a skinned Mixamo-style model under "Animated Model" (or similar): SkinnedMeshRenderers LOD0/1/2,
 *    URP/Lit materials with a baked albedo texture (_BaseMap == _MainTex) and _BaseColor = white. There is NO
 *    per-employee customisation at runtime (CharacterCustomize / BmanCustomize ... exist but aren't on employees) and no
 *    tint property to turn. EmployeeTypeColorsSO is only the HR card accent colour, not the clothes.
 *  - Clothes are separate materials (shirt, pants, shoes, jacket, waistcoat, dress, hat, helmet ...), but names lie:
 *    the Restocker's T-shirt is "Hair_Mat" (texture Low_Hair_Tshirt_Mat / Low_Hair_Yaka_Mat). Two restockers
 *    (Restocker_05/06) use a "BritneyBodyMAT" atlas that holds jeans, top, hands and face together; it is NOT recoloured
 *    (a tint can't tell skin from jeans), so those two keep their trousers.
 *
 * HOW THE CLOTHES ARE RECOLOURED (no model, no mesh and no animation is touched)
 *  - The garments' textures already have colour (blue shirts, red dresses, black suits), so multiplying _BaseColor would
 *    only darken them. Instead, once per source texture, a "shade map" is made on the CPU: the texture is copied through
 *    a RenderTexture (the originals aren't readable), each pixel's luminance is compared with the texture's mean
 *    luminance, shade = 1 + (L - mean) * 1.4 clamped to 0.45..1.45, and the map stores shade / 1.45 as grey (alpha is
 *    kept). Folds, seams and logos survive as light/dark variation. The map is compressed to DXT5 (about 1 MB each).
 *  - For every (original material, employee type) a material copy is made whose _BaseMap/_MainTex is the shade map and
 *    whose _BaseColor/_Color is chosen colour * 1.45 * garment shade. The renderer's sharedMaterials entry is swapped.
 *    Changing the colour is then only a SetColor on the copies (so the live preview is free), and "no colour" swaps the
 *    original material back. Pants are 0.7x, shoes 0.5x and waistcoats 0.8x of the colour when the ShadedGarments
 *    setting is on (so an outfit isn't one flat colour).
 *  - A sweeper (ClothesDriver, every 0.4 s) finds the live employees (Object.FindObjectsOfType per employee class) and
 *    makes every renderer match the stored colour for that employee's type. That covers employees that exist already,
 *    hired later, loaded from a save, spawned by the network on a guest, and anything that restores the original
 *    materials. A texture budget (25 ms per pass) spreads the one-off shade-map creation over a few passes.
 *
 * THE UI (clones of existing UI; the screen is a world-space canvas rendered by the PC camera, see ExtraComputers)
 *  - The Customization tab (CustomizationTab > Unlocked > Sections Scroll View > Viewport > Content, a GridLayoutGroup)
 *    holds three cards: Store Name UI (ChangeNameItem), Enterence Position UI, Enterence Apperence UI. A fourth card is
 *    made by instantiating the Store Name card under an inactive holder (no Awake runs), destroying its ChangeNameItem,
 *    the Localize components and the button's listeners, and then re-parenting it. It is injected by a postfix on
 *    CustomizationTab.OnEnable (fallback: a poll in the driver).
 *  - The $500 is charged the way the game charges the store-name change (ChangeNameOverlay.ApproveStoreName):
 *    MoneyManager.MoneyTransition(-price, UPGRADE_COSTS, true) on the host / solo player; MoneyManager already broadcasts
 *    to guests. It is charged each time the card is used, not when applying.
 *  - The panel (a child of the tab, over a clone of the tab background) has: a RenderTexture preview, arrows to switch
 *    between the 8 employee types (HrEmployeeKind), the game's own ColorPicker widget (cloned from the store-name colour
 *    picker in the NameChangeCanvas; its Image material is reset so it makes its own), 12 swatches (a fallback if the
 *    picker's shader doesn't work), an "Apply to all" toggle, Apply, Reset and Done.
 *  - The preview: a camera far below the map (y = -5000, an unnamed layer) renders to a 1024x512 RenderTexture. It sees
 *    clones of the model sub-tree ("Animated Model", found as the top Animator) of every prefab of the selected type, in a
 *    row, rotating slowly, lit by two point lights (limited range, so the store isn't lit; URP ignores light layer masks).
 *    The clones use their own material copies (preview slot) so dragging the picker never changes real employees.
 *
 * MULTIPLAYER
 *  - The host runs the employees; guests have their own copies of the same models (Photon instantiates the networked
 *    prefab), and materials are local, so the colour must be applied on every client that wants to see it.
 *  - Only the host (or the solo player) can change the colours (card disabled for guests: a guest's choice would need a
 *    custom RPC and a way to charge the host). The host publishes the colours as the Photon ROOM property
 *    "oscar.employeeclothes" (a string like "Restocker=ff8800;Cashier=2255aa"). The game itself keeps "Money",
 *    "roomStartTime" and "RackCount" there and every room-property listener only reads its own key, so unmodded clients
 *    ignore ours. Late joiners get it from the room; modded guests poll it every 0.4 s and apply it. Unmodded guests
 *    see the original colours (harmless).
 *  - Persistence: the host's colours are saved in BepInEx/config/oscar.employeeclothes.saves.txt, one line per save file
 *    (SaveManager.CurrentSaveFilePath's file name), because the game's own save format can't be extended. A new game in
 *    a used slot inherits that slot's colours (use Reset, or delete the line).
 *
 * Patches are few on purpose: CustomizationTab.OnEnable (inject the card) and IDManager.Awake (a new game scene: forget
 * cached objects). Everything else is polling and public API calls, so a game update breaks little.
 */
using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace EmployeeClothes;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.employeeclothes";
    public const string Name = "Employee Clothes";
    public const string Version = "1.0.0";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> Price;
    internal static ConfigEntry<bool> ShadedGarments;
    internal static ConfigEntry<bool> SyncToGuests;
    internal static ConfigEntry<float> PreviewLight;
    internal static ConfigEntry<bool> Verbose;

    /// <summary>True while the patches are applied and the mod does its work.</summary>
    internal static bool Active { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. Turning it off puts the employees' original clothes back and removes the card from the " +
            "computer's Customization tab right away (no restart); turning it back on restores everything.");
        Price = Config.Bind("Customization", "Price", 500f,
            new ConfigDescription(
                "What the Employee Clothes card in the computer's Customization tab costs each time you open it " +
                "(applying and changing colours inside the screen is free). Type a number; 0 makes it free.",
                new ClampedFloat(0f, 1000000f)));
        ShadedGarments = Config.Bind("Appearance", "ShadedGarments", true,
            "When on, trousers, shoes and waistcoats are a darker shade of the chosen colour (trousers 70%, " +
            "waistcoat 80%, shoes 50%), so the outfit isn't one flat colour. When off, every garment is exactly " +
            "the chosen colour (still shaded by its folds and seams).");
        SyncToGuests = Config.Bind("Multiplayer", "SyncToGuests", true,
            "Host only: share the chosen colours with the guests (as a Photon room property), so guests who also have " +
            "this mod see the same colours. Guests without the mod simply see the original clothes.");
        PreviewLight = Config.Bind("Appearance", "PreviewLight", 30f,
            new ConfigDescription(
                "Brightness of the two lights in the model preview. If the preview looks too dark or washed out, " +
                "type a different number (1 to 300).",
                new ClampedFloat(1f, 300f)));
        Verbose = Config.Bind("Diagnostics", "VerboseLogging", true,
            "Log what the mod does (tag [EmployeeClothes] in BepInEx/LogOutput.log). On while the mod is new; " +
            "turn it off once everything works.");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);
        PreviewLight.SettingChanged += (_, _) => Preview.ApplyLightSetting();
        ShadedGarments.SettingChanged += (_, _) => Recolor.OnShadeSettingChanged();

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
        if (Verbose != null && Verbose.Value) Logger?.LogInfo("[EmployeeClothes] " + msg);
    }
}
