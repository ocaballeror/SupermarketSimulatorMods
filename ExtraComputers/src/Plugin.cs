using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace ExtraComputers;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.extracomputers";
    public const string Name = "Extra Computers";
    public const string Version = "1.0.0";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> Price;
    internal static ConfigEntry<bool> Verbose;

    internal static bool Active { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. Turning it off removes all patches immediately (no restart needed). " +
            "Computers already placed stay in the store as furniture but can't be used until it is turned back on. " +
            "If the game starts with this off, saved extra computers are not loaded (and are lost when the game saves).");
        Price = Config.Bind("ExtraComputers", "Price", 500f,
            new ConfigDescription("Price of one extra computer in the furniture shop.",
                new AcceptableValueRange<float>(0f, 100000f)));
        Verbose = Config.Bind("Diagnostics", "VerboseLogging", true,
            "Log how the computer furniture is built and every time an extra computer is used " +
            "(tag [ExtraComputers] in LogOutput.log).");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);
        Price.SettingChanged += (_, _) => ComputerFurniture.ApplyPrice();

        SetActive(Enabled.Value);
        Log.LogInfo($"{Name} {Version} loaded ({(Active ? "enabled" : "disabled")})");
    }

    private void SetActive(bool on)
    {
        if (on == Active) return;
        if (on) ApplyPatches();
        else
        {
            ComputerSession.ForceEnd("mod disabled");
            _harmony.UnpatchSelf();
            Log.LogInfo("All patches removed (disabled)");
        }
        Active = on;
        ComputerSession.Reset();
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

    internal static void Info(string msg) => Logger.LogInfo("[ExtraComputers] " + msg);
    internal static void Debug(string msg) { if (Verbose.Value) Logger.LogInfo("[ExtraComputers] " + msg); }
    internal static void Warn(string msg) => Logger.LogWarning("[ExtraComputers] " + msg);
    internal static void Error(string msg) => Logger.LogError("[ExtraComputers] " + msg);
}
