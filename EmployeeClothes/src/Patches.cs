using System;
using HarmonyLib;
using TabComponent = __Project__.Scripts.Computer.Management.CustomizationTab.CustomizationTab;
using ComputerT = __Project__.Scripts.Computer.Computer;

namespace EmployeeClothes;

/// <summary>The Customization tab was shown: make sure its cards aren't left hidden, and add the Employee Clothes card if it isn't there yet.</summary>
[HarmonyPatch(typeof(TabComponent), nameof(TabComponent.OnEnable))]
internal static class CustomizationTabEnablePatch
{
    private static void Postfix(TabComponent __instance)
    {
        try { TabUi.EnsureCardsVisible(__instance); }
        catch (Exception e) { Plugin.Error("Customization tab visibility check failed: " + e); }
        try { TabUi.Ensure(__instance); }
        catch (Exception e) { Plugin.Error("Customization tab hook failed: " + e); }
    }
}

/// <summary>
/// A new game scene is starting: its objects are new, so everything cached from the previous one (material copies, shade
/// maps, the injected UI, the preview stage) is dropped. The original materials go back first, in case the old
/// employees still exist.
/// </summary>
[HarmonyPatch(typeof(IDManager), nameof(IDManager.Awake))]
internal static class NewSceneResetPatch
{
    private static void Postfix()
    {
        try
        {
            Employees.RestoreAll();
            Preview.Forget();
            TabUi.Forget();
            Recolor.DestroyAll();
            ClothesStore.OnNewScene();
            Plugin.Debug("New scene: caches reset");
        }
        catch (Exception e) { Plugin.Error("Scene reset failed: " + e); }
    }
}

/// <summary>The player left the computer (Computer.OpenPC set to false): the paid session is over, at once.</summary>
[HarmonyPatch(typeof(ComputerT), nameof(ComputerT.OpenPC), MethodType.Setter)]
internal static class ComputerClosePatch
{
    private static void Postfix(bool value)
    {
        try { if (!value) TabUi.EndSession("left the computer"); }
        catch (Exception e) { Plugin.Error("Computer close hook failed: " + e); }
    }
}

/// <summary>
/// The game wrote a save file (SaveManager.Save(string), private, the leaf that every save path ends in: autosave, quick
/// save, menu save, new game). The colors are committed to the mod's file here and only here, so they stay tied to the
/// game save: quitting without saving takes back the money and the colors together.
/// </summary>
[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save), new[] { typeof(string) })]
internal static class SaveWrittenPatch
{
    private static void Postfix(string saveName)
    {
        try { ClothesStore.OnGameSaved(saveName); }
        catch (Exception e) { Plugin.Error("Save hook failed: " + e); }
    }
}

/// <summary>SaveManager.Clear resets the game data (main menu start, new game, restart after game over): the colors of the previous game go with it.</summary>
[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Clear))]
internal static class SaveClearedPatch
{
    private static void Postfix()
    {
        try { ClothesStore.OnGameDataCleared(); }
        catch (Exception e) { Plugin.Error("Save clear hook failed: " + e); }
    }
}

/// <summary>SaveManager.ApplySaveData applies an existing save (continue, load, load for multiplayer); a new game also calls it, but Clear comes after it.</summary>
[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.ApplySaveData))]
internal static class SaveAppliedPatch
{
    private static void Postfix()
    {
        try { ClothesStore.OnSaveDataApplied(); }
        catch (Exception e) { Plugin.Error("Save apply hook failed: " + e); }
    }
}
