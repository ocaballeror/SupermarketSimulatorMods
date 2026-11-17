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
    public const string Guid = "oscar.autocourierload";
    public const string Name = "Auto Courier Load";
    public const string Version = "0.2.0";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> ScanRadius;
    internal static ConfigEntry<int> MaxBags;
    internal static ConfigEntry<float> LeaveUnderSeconds;
    internal static ConfigEntry<float> KeepScanningSeconds;
    internal static ConfigEntry<bool> LeaveWhenFull;
    internal static ConfigEntry<bool> Diagnostics;

    /// <summary>True while the mod's patches are applied.</summary>
    internal static bool Active { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. Turning it off removes the mod's patches immediately (no restart needed); " +
            "turning it back on re-applies them. Only the host (or a solo player) does anything.");
        ScanRadius = Config.Bind("AutoCourierLoad", "ScanRadius", 4f,
            new ConfigDescription(
                "How far from the courier's parking space (in metres) he looks for closed, name-tagged " +
                "delivery bags when he comes back from a delivery. Type a number, 0.5 to 20.",
                new ClampedFloat(0.5f, 20f)));
        MaxBags = Config.Bind("AutoCourierLoad", "MaxBags", 0,
            new ConfigDescription(
                "The most bags he loads in his trunk this way. 0 = as many as his trunk holds (3 at the " +
                "start, more when he levels up). A number above his trunk size changes nothing. Bags that " +
                "you hand him yourself count too.",
                new ClampedInt(0, 20)));
        LeaveUnderSeconds = Config.Bind("AutoCourierLoad", "LeaveUnderSeconds", 60f,
            new ConfigDescription(
                "After he has loaded something, he leaves at once if one of the orders in his trunk has " +
                "less than this many seconds left (the countdown shown on the order's note, in real " +
                "seconds). Type a number, 0 to 600. 0 = never leave because of a deadline.",
                new ClampedFloat(0f, 600f)));
        LeaveWhenFull = Config.Bind("AutoCourierLoad", "LeaveWhenFull", false,
            "He also leaves as soon as his trunk is full, even if other orders are still active, instead of " +
            "waiting for you to press E or for a deadline. Off by default: he then leaves only when his " +
            "trunk covers every active order or an order is about to run out. \"Full\" follows MaxBags if " +
            "that is lower than his trunk size.");
        KeepScanningSeconds = Config.Bind("AutoCourierLoad", "KeepScanningSeconds", 30f,
            new ConfigDescription(
                "After he is back at his parking space, bags that you put down there within this many " +
                "seconds are still picked up. Type a number, 0 to 600. 0 = he looks only once, right " +
                "when he arrives. Once he has loaded something he keeps looking for as long as he waits.",
                new ClampedFloat(0f, 600f)));
        Diagnostics = Config.Bind("Diagnostics", "Diagnostics", true,
            "Log [AutoCourier] lines to BepInEx/LogOutput.log: arrival, each bag loaded, why he leaves. " +
            "Turn off once the mod is confirmed to work.");

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
