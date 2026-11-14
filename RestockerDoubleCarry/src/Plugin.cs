using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace RestockerDoubleCarry;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.restockerdoublecarry";
    public const string Name = "Restocker Double Carry";
    public const string Version = "1.0.0";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<int> MinimumLevel;
    internal static ConfigEntry<bool> StackVisuals;
    internal static ConfigEntry<bool> Verbose;

    /// <summary>True while the mod's patches are applied.</summary>
    internal static bool Active { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. Turning it off removes the feature immediately (no restart needed). " +
            "A restocker that is carrying two boxes at that moment puts the second one down on the floor.");
        MinimumLevel = Config.Bind("Restockers", "MinimumLevel", 10,
            new ConfigDescription(
                "Restockers at this level or higher fetch two boxes per trip: they pick up two boxes of different " +
                "products, restock the first product, then the second, then put back whatever is left. " +
                "Lower-level restockers work as normal.",
                new AcceptableValueRange<int>(1, 100)));
        StackVisuals = Config.Bind("Restockers", "StackSecondBox", true,
            "Show the second box stacked on top of the box in the restocker's hands. " +
            "On a guest this also fixes how the host's restockers look (both boxes visible instead of one inside the other).");
        Verbose = Config.Bind("Diagnostics", "VerboseLogging", true,
            "Log each step of a two-box trip (tag [DoubleCarry] in BepInEx/LogOutput.log).");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);

        SetActive(Enabled.Value);
        Log.LogInfo($"{Name} {Version} loaded ({(Active ? "enabled" : "disabled")}, minimum level {MinimumLevel.Value})");
    }

    private void SetActive(bool on)
    {
        if (on == Active) return;
        if (on) ApplyPatches();
        else
        {
            // Hand back any second box while the patches still exist, then remove them.
            DoubleCarry.ReleaseAll();
            _harmony.UnpatchSelf();
            Log.LogInfo("All patches removed (disabled)");
        }
        Active = on;
        DoubleCarry.ResetState();
    }

    private void ApplyPatches()
    {
        // Each [HarmonyPatch] class is applied on its own, so one broken patch (e.g. after a game
        // update) is logged without disabling the others.
        int ok = 0, total = 0;
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            total++;
            try
            {
                _harmony.CreateClassProcessor(type).Patch();
                ok++;
                Log.LogDebug($"Applied {type.FullName}");
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to apply {type.FullName}: {e}");
            }
        }
        Log.LogInfo($"Patches applied ({ok}/{total})");
    }

    public override bool Unload()
    {
        SetActive(false);
        return true;
    }
}
