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

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. Turning it off removes all patches immediately (no restart needed).");

        ModifierKey = Config.Bind("Keys", "ModifierKey", Key.LeftShift,
            "Hold this key while clicking to move everything at once. Modifier + Take (right click by default) " +
            "fills the box / paper bag / crate from the shelf you look at; modifier + Use (left click by default) " +
            "empties it onto the shelf. The clicks are the game's own Take and Use bindings, so rebinding them in " +
            "the game's settings also changes them here. Set to None to make every Take/Use click a bulk transfer.");
        BulkTake = Config.Bind("Keys", "BulkTake", true,
            "Modifier + Take moves all matching products from the shelf into the held container.");
        BulkPlace = Config.Bind("Keys", "BulkPlace", true,
            "Modifier + Use moves all matching products from the held container onto the shelf.");

        Boxes = Config.Bind("Containers", "Boxes", true, "Bulk transfer with product boxes (crates bought from the computer).");
        PaperBags = Config.Bind("Containers", "PaperBags", true, "Bulk transfer with online-order paper bags.");
        Crates = Config.Bind("Containers", "Crates", true, "Bulk transfer with the multi-product crate.");

        SecondsBetweenItems = Config.Bind("Speed", "SecondsBetweenItems", 0.02f,
            new ConfigDescription("Time between two products during a bulk transfer, in seconds (the game's own " +
                                  "hold-to-repeat speed is roughly 0.1-0.2 s per product). 0 = one product per frame.",
                new AcceptableValueRange<float>(0f, 0.5f)));
        AnimationSpeed = Config.Bind("Speed", "AnimationSpeed", 5f,
            new ConfigDescription("How much faster the products fly between the shelf and the container during a " +
                                  "bulk transfer (1 = the game's normal animation). It is the game's own animation, " +
                                  "only played faster, and only on your screen.",
                new AcceptableValueRange<float>(1f, 20f)));

        Verbose = Config.Bind("Diagnostics", "VerboseLogging", true,
            "Log each bulk transfer (start, how many products, why it stopped) with the tag [Bulk] in LogOutput.log.");

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
