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
    public const string Guid = "oscar.bulkshelftransfer";
    public const string Name = "Bulk Shelf Transfer";
    public const string Version = "0.2.0";

    internal static ManualLogSource Logger;

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<Key> ModifierKey;
    internal static ConfigEntry<bool> BulkTake;
    internal static ConfigEntry<bool> BulkPlace;
    internal static ConfigEntry<bool> Boxes;
    internal static ConfigEntry<bool> PaperBags;
    internal static ConfigEntry<bool> Crates;
    internal static ConfigEntry<float> SecondsBetweenItems;
    internal static ConfigEntry<float> AnimationSpeed;
    internal static ConfigEntry<bool> Verbose;

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
            "empties it onto the shelf".)
        BulkTake = true;
            "Modifier + Take moves all matching products from the shelf into the held container.");
        BulkPlace = true;

        Boxes = true;
        PaperBags = true;
        Crates = true;

        SecondsBetweenItems = 0.2f;
        AnimationSpeed = 5f;

        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", true,
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
