using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace PlayerDoubleCarry;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.playerdoublecarry";
    public const string Name = "Player Double Carry";
    public const string Version = "0.1.0";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> RemoteStackVisuals;
    internal static ConfigEntry<bool> Verbose;

    /// <summary>True while the mod's patches are applied.</summary>
    internal static bool Active { get; private set; }

    private static Plugin s_instance;
    private Harmony _harmony;
    /// <summary>Turned off in the config while the player still had two boxes: unpatch once that is over.</summary>
    private static bool s_disableWhenIdle;

    public override void Load()
    {
        s_instance = this;
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. While holding a closed box, left-click (Use) another box to pick it up too. " +
            "Turning it off takes effect immediately (no restart); if you are carrying two boxes at that moment, " +
            "the second box still comes back to your hands when you put the first one down.");
        RemoteStackVisuals = Config.Bind("Carry", "ShowOtherPlayersStack", true,
            "Show the two boxes another player is carrying stacked (one under the other) instead of inside each other. " +
            "Cosmetic, only affects what you see.");
        Verbose = Config.Bind("Diagnostics", "VerboseLogging", true,
            "Log each step of a two-box carry (tag [PlayerCarry] in BepInEx/LogOutput.log).");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);

        AddComponent<CarryBehaviour>();
        SetActive(Enabled.Value);
        Log.LogInfo($"{Name} {Version} loaded ({(Active ? "enabled" : "disabled")})");
    }

    private void SetActive(bool on)
    {
        s_disableWhenIdle = false;
        if (on == Active) return;
        if (on)
        {
            DoubleCarry.ResetState();
            ApplyPatches();
            Active = true;
            return;
        }
        if (DoubleCarry.Busy)
        {
            // The patches are what hands the second box back; keep them until that has happened.
            s_disableWhenIdle = true;
            Log.LogInfo("[PlayerCarry] Disabled: no new second boxes. The mod turns off fully once the box you are carrying " +
                        "under the first one is back in your hands.");
            return;
        }
        Unpatch();
    }

    private void Unpatch()
    {
        _harmony.UnpatchSelf();
        Active = false;
        DoubleCarry.ResetState();
        Log.LogInfo("All patches removed (disabled)");
    }

    /// <summary>Called every frame by CarryBehaviour.</summary>
    internal static void Tick()
    {
        if (!Active) return;
        DoubleCarry.Tick();
        if (s_disableWhenIdle && !DoubleCarry.Busy && !Enabled.Value)
        {
            s_disableWhenIdle = false;
            s_instance?.Unpatch();
        }
    }

    private void ApplyPatches()
    {
        // Each [HarmonyPatch] class is applied on its own, so one broken patch (e.g. after a game update)
        // is logged without disabling the others.
        int ok = 0, total = 0;
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            total++;
            try
            {
                _harmony.CreateClassProcessor(type).Patch();
                ok++;
                Log.LogDebug($"Applied {type.FullName}");
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to apply {type.FullName}: {e}");
            }
        }
        Log.LogInfo($"Patches applied ({ok}/{total})");
    }

    public override bool Unload()
    {
        _harmony?.UnpatchSelf();
        Active = false;
        DoubleCarry.ResetState();
        return true;
    }
}
