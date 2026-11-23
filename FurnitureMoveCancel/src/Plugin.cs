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
    public const string Guid = "flamboyanthopper.furnituremovecancel";
    public const string Name = "Furniture Move Cancel";
    public const string Version = "1.0.5";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> VerboseLogging;

    internal static bool Active { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");
        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Enable verbose logging for debugging purposes.");

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
