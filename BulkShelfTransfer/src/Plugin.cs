using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace BulkShelfTransfer;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "flamboyanthopper.bulkshelftransfer";
    public const string Name = "Bulk Shelf Transfer";
    public const string Version = "0.2.3";

    internal static ManualLogSource Logger;

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<Key> ModifierKey;
    internal static ConfigEntry<bool> VerboseLogging;

    // Fixed values (no longer settings).
    internal const bool BulkTake = true;
    internal const bool BulkPlace = true;
    internal const bool Boxes = true;
    internal const bool PaperBags = true;
    internal const bool Crates = true;
    internal const float SecondsBetweenItems = 0.02f;
    internal static readonly float AnimationSpeed = 5f;

    internal static bool Active { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");

        ModifierKey = Config.Bind("Keys", "ModifierKey", Key.LeftShift,
            "Hold this key while clicking to move everything at once. Modifier + Take (default right click) " +
            "fills the box / paper bag / crate from the shelf you look at; modifier + Use (default left click) " +
            "empties it onto the shelf.");

        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Enable verbose logging for debugging purposes.");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);

        SetActive(Enabled.Value);
        Log.LogInfo($"{Name} {Version} loaded ({(Active ? "enabled" : "disabled")})");
    }

    private void SetActive(bool on)
    {
        if (on == Active) return;
        ResetState();
        if (on) ApplyPatches();
        else
        {
            _harmony.UnpatchSelf();
            Log.LogInfo("All patches removed (disabled)");
        }
        Active = on;
    }

    private static void ResetState()
    {
        BoxBulk.Reset();
        BagBulk.Reset();
        CrateBulk.Reset();
        TweenBoost.Reset();
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
        try { CrateBulk.PatchCoroutines(_harmony); }
        catch (Exception e) { Log.LogError($"Failed to patch the crate coroutines: {e}"); }
        Log.LogInfo("Patches applied");
    }

    public override bool Unload()
    {
        SetActive(false);
        return true;
    }
}
