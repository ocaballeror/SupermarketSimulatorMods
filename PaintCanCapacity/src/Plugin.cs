using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace PaintCanCapacity;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "flamboyanthopper.paintcancapacity";
    public const string Name = "Paint Can Capacity";
    public const string Version = "1.0.6";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> Multiplier;
    internal static ConfigEntry<bool> VerboseLogging;

    /// <summary>True while the mod's patches are applied.</summary>
    internal static bool Active { get; private set; }

    /// <summary>False during Load(); live setting changes after that may touch game objects.</summary>
    internal static bool LoadFinished { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");
        Multiplier = Config.Bind("Paint", "CapacityMultiplier", 2f,
            new ConfigDescription(
                "How many times longer a paint can lasts. 2 = twice as many walls per can. " +
                "Applies to cans bought after the change; cans you already own keep the paint they have.",
                // ClampedFloat instead of AcceptableValueRange: config managers draw a text field, not a slider.
                new ClampedFloat(1f, 10f)));
        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Enable verbose logging for debugging purposes.");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);
        Multiplier.SettingChanged += (_, _) => { if (Active) PaintPatches.ApplyToAll(); };

        SetActive(Enabled.Value);
        LoadFinished = true;
        Log.LogInfo($"{Name} {Version} loaded ({(Active ? "enabled" : "disabled")}, x{Multiplier.Value})");
    }

    private void SetActive(bool on)
    {
        if (on == Active) return;
        if (on)
        {
            ApplyPatches();
            Active = true;
            PaintPatches.ApplyToAll();
        }
        else
        {
            _harmony.UnpatchSelf();
            Active = false;
            PaintPatches.RestoreAll();
            Log.LogInfo("All patches removed (disabled)");
        }
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
