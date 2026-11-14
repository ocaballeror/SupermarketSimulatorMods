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
    public const string Version = "1.0.0";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> SpeedMultiplier;
    internal static ConfigEntry<bool> Verbose;

    internal static bool Active { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. Turning it off removes all patches immediately (no restart needed).");
        SpeedMultiplier = Config.Bind("SelfCheckout", "SpeedMultiplier", 2f,
            new ConfigDescription(
                "How much faster customers get through self checkout machines (scanning each item and paying). " +
                "1 = normal speed, 2 = twice as fast, up to 10.",
                new AcceptableValueRange<float>(1f, 10f)));
        Verbose = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Log every shortened self checkout wait (tag [SelfCheckout] in LogOutput.log). Very chatty.");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);

        SetActive(Enabled.Value);
        Log.LogInfo($"{Name} {Version} loaded ({(Active ? "enabled" : "disabled")}, x{SpeedMultiplier.Value})");
    }

    private void SetActive(bool on)
    {
        if (on == Active) return;
        if (on) ApplyPatches();
        else
        {
            _harmony.UnpatchSelf();
            Log.LogInfo("All patches removed (disabled)");
        }
        Active = on;
    }

    private void ApplyPatches()
    {
        // The coroutine classes are compiler generated and get obfuscated names in the interop
        // assembly, so they are located by their native names at runtime.
        int n = SelfCheckoutPatches.Apply(_harmony);
        Log.LogInfo($"Patches applied ({n}/{SelfCheckoutPatches.Targets.Length} self checkout coroutines)");
    }

    public override bool Unload()
    {
        SetActive(false);
        return true;
    }
}
