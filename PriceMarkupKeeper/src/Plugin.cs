using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace PriceMarkupKeeper;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.pricemarkupkeeper";
    public const string Name = "Price Markup Keeper";
    public const string Version = "1.1.1";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> KeepMarkup;
    internal static ConfigEntry<bool> OnlyFollowingProducts;
    internal static ConfigEntry<bool> PriceNewProducts;
    internal static ConfigEntry<bool> Verbose;

    internal static bool Active { get; private set; }

    private Harmony _harmony;

    private static readonly Type[] PatchClasses =
    {
        typeof(TryChangingBasePricesPatch),
        typeof(ChangeCurrentCostPatch),
        typeof(PurchaseLicensePatch),
        typeof(PurchaseLicenseOrderPatch),
    };

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");
        KeepMarkup = true;
        OnlyFollowingProducts = true;
        PriceNewProducts = true;
        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", true,
            "Enable verbose logging for debugging purposes.");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);

        SetActive(Enabled.Value);
        Log.LogInfo($"{Name} {Version} loaded ({(Active ? "enabled" : "disabled")})");
    }

    private void SetActive(bool on)
    {
        if (on == Active) return;
        if (on)
        {
            int ok = 0;
            foreach (var t in PatchClasses)
            {
                try { _harmony.CreateClassProcessor(t).Patch(); ok++; }
                catch (Exception e) { Log.LogError($"Failed to apply {t.Name}: {e}"); }
            }
            Log.LogInfo($"Patches applied ({ok}/{PatchClasses.Length})");
        }
        else
        {
            _harmony.UnpatchSelf();
            Log.LogInfo("All patches removed (disabled)");
        }
        MarkupKeeper.Reset();
        Active = on;
    }

    public override bool Unload()
    {
        SetActive(false);
        return true;
    }
}
