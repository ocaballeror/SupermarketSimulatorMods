using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace AutoLights;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "flamboyanthopper.autolights";
    public const string Name = "Auto Lights";
    public const string Version = "1.0.0";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> VerboseLogging;

    // Fixed values (not settings).
    /// <summary>In-game time of day (24 h clock, minutes since midnight) at which the lights are switched on: 19:00.</summary>
    internal const int TriggerMinutes = 19 * 60;
    /// <summary>The largest forward step of the in-game clock between two frames that still counts as time passing
    /// (a save load or any other jump of the clock is not a "crossing 19:00").</summary>
    internal const int MaxStepMinutes = 120;

    public override void Load()
    {
        Logger = Log;

        Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");
        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Enable verbose logging for debugging purposes.");

        // The mod has no patches: a per-frame behaviour reads the clock and does nothing while disabled,
        // so toggling Enabled works live. Nothing touches the game from Load().
        Enabled.SettingChanged += (_, _) => LightsTimer.Reset();
        AddComponent<LightsBehaviour>();
        Log.LogInfo($"{Name} {Version} loaded ({(Enabled.Value ? "enabled" : "disabled")})");
    }

    internal static void Vlog(string msg)
    {
        if (VerboseLogging != null && VerboseLogging.Value) Logger.LogInfo(msg);
    }

    public override bool Unload()
    {
        LightsTimer.Reset();
        return true;
    }
}
