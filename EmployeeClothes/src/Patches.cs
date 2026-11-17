using System;
using HarmonyLib;
using TabComponent = __Project__.Scripts.Computer.Management.CustomizationTab.CustomizationTab;

namespace EmployeeClothes;

/// <summary>The Customization tab was shown: add the Employee Clothes card if it isn't there yet.</summary>
[HarmonyPatch(typeof(TabComponent), nameof(TabComponent.OnEnable))]
internal static class CustomizationTabEnablePatch
{
    private static void Postfix(TabComponent __instance)
    {
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
