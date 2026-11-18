using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace PackUpProducts;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.packupproducts";
    public const string Name = "Pack Up Products";
    public const string Version = "0.1.1";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<string> TriggerAction;
    internal static ConfigEntry<bool> Verbose;

    internal static bool Unloaded { get; private set; }

    public override void Load()
    {
        Logger = Log;

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. With your hands empty, right clicking a product on a shelf, fridge or freezer " +
            "puts it into a new box of the right size and picks the box up. Takes effect right away.");
        TriggerAction = Config.Bind("General", "TriggerAction", "Take",
            "The game input action that triggers packing. \"Take\" is the game's own Take action (right mouse " +
            "button by default) and follows any key you rebound it to in the game's controls menu. " +
            "Other action names from the game's Player map also work (for example \"Use\" is left click).");
        Verbose = Config.Bind("Diagnostics", "VerboseLogging", true,
            "Log every step of packing (tag [PackUp] in LogOutput.log), including why a click was ignored. " +
            "Only clicks aimed at a shelf with empty hands are logged.");

        AddComponent<PackBehaviour>();
        Log.LogInfo($"{Name} {Version} loaded ({(Enabled.Value ? "enabled" : "disabled")}, action \"{TriggerAction.Value}\")");
    }

    public override bool Unload()
    {
        Unloaded = true;
        PackUp.Reset();
        return true;
    }
}

/// <summary>Per-frame driver. Registered into IL2CPP by BasePlugin.AddComponent.</summary>
public class PackBehaviour : MonoBehaviour
{
    public PackBehaviour(IntPtr ptr) : base(ptr) { }

    private static float _nextErrorLog;

    private void Update()
    {
        if (Plugin.Unloaded) return;
        try { PackUp.Update(); }
        catch (Exception e)
        {
            // Don't flood the log at 60 fps if something keeps failing.
            if (Time.unscaledTime < _nextErrorLog) return;
            _nextErrorLog = Time.unscaledTime + 5f;
            Plugin.Logger.LogError($"[PackUp] Update failed: {e}");
        }
    }
}
