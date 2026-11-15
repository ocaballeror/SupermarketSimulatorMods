using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace GridModeDefault;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.gridmodedefault";
    public const string Name = "Grid Mode Default";
    public const string Version = "1.0.2";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> RememberToggle;
    internal static ConfigEntry<bool> Verbose;

    internal static bool Active { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. Turning it off removes all patches immediately and restores the game's own " +
            "grid setting (no restart needed).");
        RememberToggle = Config.Bind("GridMode", "RememberToggle", true,
            "true: the grid starts on, and if you turn it off with the grid key (X by default) it stays off " +
            "until you turn it on again or restart the game (the game's own behaviour). " +
            "false: every time you pick up or place furniture the grid starts on again.");
        Verbose = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Log what the mod does when furniture placement starts (tag [GridMode] in LogOutput.log).");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);

        SetActive(Enabled.Value);
        Log.LogInfo($"{Name} {Version} loaded ({(Active ? "enabled" : "disabled")})");
    }

    private void SetActive(bool on)
    {
        if (on == Active) return;
        if (on) ApplyPatches();
        else
        {
            _harmony.UnpatchSelf();
            GridDefaultPatch.Restore();
            Log.LogInfo("All patches removed (disabled)");
        }
        Active = on;
        GridDefaultPatch.Reset();
    }

    private void ApplyPatches()
    {
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try
            {
                _harmony.CreateClassProcessor(type).Patch();
                Log.LogDebug($"Applied {type.FullName}");
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to apply {type.FullName}: {e}");
            }
        }
        Log.LogInfo("Patches applied");
    }

    public override bool Unload()
    {
        SetActive(false);
        return true;
    }
}
