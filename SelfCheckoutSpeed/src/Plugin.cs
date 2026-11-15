using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SelfCheckoutSpeed;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.selfcheckoutspeed";
    public const string Name = "Self Checkout Speed";
    public const string Version = "1.1.0";

    internal const float MaxMultiplier = 10f;

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> SpeedMultiplier;
    internal static ConfigEntry<bool> SpeedUpEmployeeHelp;
    internal static ConfigEntry<bool> Verbose;

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true,
            "Speeds up self checkouts when you host or play solo. As a guest in multiplayer you always run " +
            "at the host's speed (so both games stay in step), whatever this and the other settings say.");
        // ClampedFloat instead of AcceptableValueRange: config managers draw a text field, not a slider.
        SpeedMultiplier = Config.Bind("SelfCheckout", "SpeedMultiplier", 2f,
            new ConfigDescription(
                "How much faster customers get through self checkout machines (scanning each item and paying). " +
                "Type a number: 1 = normal speed, 2 = twice as fast, 1.5 = one and a half times, up to 10.",
                new ClampedFloat(1f, MaxMultiplier)));
        SpeedUpEmployeeHelp = Config.Bind("SelfCheckout", "SpeedUpEmployeeHelp", true,
            "Also speed up scanning when an employee (customer helper) helps a customer at a self checkout, " +
            "by the same multiplier.");
        Verbose = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Log every shortened self checkout wait (tag [SelfCheckout] in LogOutput.log). Very chatty.");

        Enabled.SettingChanged += (_, _) => OnSettingsChanged();
        SpeedMultiplier.SettingChanged += (_, _) => OnSettingsChanged();
        SpeedUpEmployeeHelp.SettingChanged += (_, _) => OnSettingsChanged();

        // The patches stay installed even when Enabled is off: a guest must follow the host's speed
        // whatever its own settings say (see SelfCheckoutPatches.cs). With nothing to speed up they
        // return straight away.
        int n = SelfCheckoutPatches.Apply(_harmony);
        Log.LogInfo($"Patches applied ({n}/{SelfCheckoutPatches.TargetCount} checkout coroutines)");
        AddComponent<SyncBehaviour>();

        Log.LogInfo($"{Name} {Version} loaded ({(Enabled.Value ? "enabled" : "disabled when hosting/solo")}, " +
                    $"x{SpeedMultiplier.Value}, employee help {(SpeedUpEmployeeHelp.Value ? "on" : "off")})");
    }

    private static void OnSettingsChanged()
    {
        HostSync.Invalidate();
        Logger.LogInfo($"Settings: {(Enabled.Value ? "enabled" : "disabled")}, x{SpeedMultiplier.Value}, " +
                       $"employee help {(SpeedUpEmployeeHelp.Value ? "on" : "off")}" +
                       (HostSync.IsGuest ? " (you are a guest: the host's speed is used)" : ""));
    }

    public override bool Unload()
    {
        _harmony.UnpatchSelf();
        HostSync.Reset();
        return true;
    }
}
