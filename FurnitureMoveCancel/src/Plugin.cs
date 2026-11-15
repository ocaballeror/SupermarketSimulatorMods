using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace FurnitureMoveCancel;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.furnituremovecancel";
    public const string Name = "Furniture Move Cancel";
    public const string Version = "1.0.2";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> ShowHint;
    internal static ConfigEntry<float> ReturnDelay;
    internal static ConfigEntry<bool> Verbose;

    internal static bool Active { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. Turning it off removes all patches immediately (no restart needed).");
        ShowHint = Config.Bind("FurnitureMoveCancel", "ShowHint", true,
            "Show the Escape key in the list of key hints while moving furniture.");
        ReturnDelay = Config.Bind("FurnitureMoveCancel", "ReturnDelaySeconds", 0f,
            new ConfigDescription(
                "Optional pause at the original spot before the furniture is put down. 0 = put it down immediately. " +
                "In multiplayer, if a piece can't send its exact position to the other players, it is held " +
                "at least 0.3 s so they see it in the right place.",
                new AcceptableValueRange<float>(0f, 2f)));
        Verbose = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Log what happens when Escape is pressed while moving furniture (tag [MoveCancel] in LogOutput.log).");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);
        AddComponent<MoveCancelBehaviour>();

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
        MoveCancel.Reset();
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
