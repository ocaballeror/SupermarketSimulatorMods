using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SupermarketFixes;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "flamboyanthopper.supermarketfixes";
    public const string Name = "Supermarket Fixes";
    public const string Version = "0.11.13";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> VerboseLogging;

    /// <summary>True while the mod's patches are applied.</summary>
    internal static bool Active { get; private set; }

    /// <summary>False until <see cref="Load"/> has finished; game objects must not be touched before that.</summary>
    internal static bool Loaded { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");
        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", false, "Enable verbose logging for debugging purposes.");
        Patches.RackMoveFix.Bind(Config);
        Patches.GhostHeldItemFix.Bind(Config);
        Patches.StuckBoxFix.Bind(Config);
        Patches.BoxPlaceFix.Bind(Config);
        Patches.CratePlaceFix.Bind(Config);
        Patches.FloatingBoxFix.Bind(Config);
        Patches.JanitorCleanAlignFix.Bind(Config);
        Patches.CourierOrdersFix.Bind(Config);
        Patches.OrderBagCountsFix.Bind(Config);
        Patches.LabeledOrderFix.Bind(Config);

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);
        AddComponent<DiagnosticsBehaviour>();

        SetActive(Enabled.Value);
        Loaded = true;
        Log.LogInfo($"{Name} {Version} loaded ({(Active ? "enabled" : "disabled")})");
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
        Patches.RackMoveFix.ResetState();
        Patches.GhostHeldItemFix.ResetState();
        Patches.StuckBoxFix.ResetState();
        Patches.BoxPlaceFix.ResetState();
        Patches.CratePlaceFix.ResetState();
        Patches.FloatingBoxFix.ResetState();
        Patches.JanitorCleanAlignFix.ResetState();
        Patches.CourierOrdersFix.ResetState();
        Patches.OrderBagCountsFix.ResetState();
        Patches.LabeledOrderFix.ResetState();
    }

    private void ApplyPatches()
    {
        // Each fix lives in its own [HarmonyPatch] class under src/Patches. A failing patch is
        // logged instead of taking the whole plugin down.
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
