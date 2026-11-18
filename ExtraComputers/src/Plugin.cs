using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace ExtraComputers;

public enum IconStyle { Model, GameSymbol }

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.extracomputers";
    public const string Name = "Extra Computers";
    public const string Version = "1.4.1";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> Price;
    internal static ConfigEntry<bool> ProtectUnmodded;
    internal static ConfigEntry<bool> IndependentScreens;
    internal static ConfigEntry<bool> Verbose;
    internal static ConfigEntry<IconStyle> Icon;

    internal static bool Active { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");
        Price = 1000f;
        IndependentScreens = true;
        ProtectUnmodded = Config.Bind("Multiplayer", "ProtectUnmoddedPlayers", true,
            "The game can bug out on multiplayer if not all players have the mod. Turn this off only if everybody has the mod installed.");
        Icon = IconStyle.GameSymbol;
        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", true,
            "Enable verbose logging for debugging purposes.");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);
        Price.SettingChanged += (_, _) => ComputerFurniture.ApplyPrice();
        IndependentScreens.SettingChanged += (_, _) => ComputerUiState.Clear();
        Icon.SettingChanged += (_, _) =>
        {
            try { ComputerFurniture.ResetIcon(); }
            catch (Exception e) { Log.LogWarning($"Changing the icon failed: {e.Message}"); }
        };

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
            Presence.OnDisabled();
            _harmony.UnpatchSelf();
            Log.LogInfo("All patches removed (disabled)");
        }
        Active = on;
        ComputerSession.Reset();
        try { ComputerFurniture.OnActiveChanged(on); }
        catch (Exception e) { Log.LogWarning($"Updating the shop item failed: {e.Message}"); }
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
