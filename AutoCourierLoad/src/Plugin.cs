using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace AutoCourierLoad;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "flamboyanthopper.autocourierload";
    public const string Name = "Auto Courier Load";
    public const string Version = "0.2.4";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> ScanRadius;
    internal static ConfigEntry<bool> VerboseLogging;

    // Fixed values (no longer settings).
    internal const float LeaveUnderSeconds = 60f;

    /// <summary>True while the mod's patches are applied.</summary>
    internal static bool Active { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");
        ScanRadius = Config.Bind("AutoCourierLoad", "ScanRadius", 4f,
            new ConfigDescription(
                "How far from the courier's parking space (in metres) he looks for closed, name-tagged " +
                "delivery bags.",
                new ClampedFloat(0.5f, 20f)));
        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Enable verbose logging for debugging purposes.");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);
        AddComponent<AutoCourierBehaviour>();

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
            Log.LogInfo("All patches removed (disabled)");
        }
        Active = on;
        AutoLoad.Reset();
    }

    private void ApplyPatches()
    {
        // Each patch is its own [HarmonyPatch] class; a failing one is logged instead of taking the plugin down.
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

/// <summary>Per-frame driver for <see cref="AutoLoad"/>. Registered into IL2CPP by BasePlugin.AddComponent.</summary>
public class AutoCourierBehaviour : MonoBehaviour
{
    public AutoCourierBehaviour(IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        try { AutoLoad.Tick(); }
        catch (Exception e) { AutoLoad.LogError("Update", e); }
    }
}
