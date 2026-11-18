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
    public const string Version = "1.1.1";

    internal const float MaxMultiplier = 10f;

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> SpeedMultiplier;
    internal static ConfigEntry<bool> SpeedUpEmployeeHelp;
    internal static ConfigEntry<bool> Verbose;

    internal static bool Unloaded { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");
        // ClampedFloat instead of AcceptableValueRange: config managers draw a text field, not a slider.
        SpeedMultiplier = Config.Bind("SelfCheckout", "SpeedMultiplier", 2f,
            new ConfigDescription(
                "How much faster customers are at using self checkouts."
                new ClampedFloat(1f, MaxMultiplier)));
        SpeedUpEmployeeHelp = true;
        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", true,
            "Enable verbose logging for debugging purposes.");

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
        Unloaded = true; // stops SyncBehaviour publishing
        _harmony.UnpatchSelf();
        HostSync.Reset();
        return true;
    }
}
