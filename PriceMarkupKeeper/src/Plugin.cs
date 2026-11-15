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
    public const string Version = "1.0.1";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> KeepMarkup;
    internal static ConfigEntry<bool> OnlyFollowingProducts;
    internal static ConfigEntry<bool> Verbose;

    internal static bool Active { get; private set; }

    private Harmony _harmony;

    private static readonly Type[] PatchClasses =
    {
        typeof(TryChangingBasePricesPatch),
        typeof(ChangeCurrentCostPatch),
    };

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. Turning it off removes all patches immediately (no restart needed).");
        KeepMarkup = Config.Bind("Pricing", "KeepMarkupOnPriceChanges", true,
            "When a product's market price goes up or down at the start of a new day, set its selling price again " +
            "using the markup (% above market price) last applied from the computer's bulk pricing tab.");
        OnlyFollowingProducts = Config.Bind("Pricing", "OnlyProductsFollowingMarkup", true,
            "Only reprice products whose price still matched that markup before the change, so prices you typed in " +
            "by hand are left alone. Turn off to reprice every product whose market price changed.");
        Verbose = Config.Bind("Diagnostics", "VerboseLogging", true,
            "Log every repriced product (tag [Markup] in LogOutput.log).");

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
