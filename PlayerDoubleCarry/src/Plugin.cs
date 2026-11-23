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
    public const string Guid = "flamboyanthopper.playerdoublecarry";
    public const string Name = "Player Double Carry";
    public const string Version = "0.2.3";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> VerboseLogging;

    // Fixed behaviour (not exposed as settings).
    internal static readonly bool RemoteStackVisuals = true;
    internal static readonly bool BuyAtWholesale = true;

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

        Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");
        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Enable verbose logging for debugging purposes.");

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
        if (DoubleCarry.HasWork)
        {
            // The patches are what hands the second box back (and keep a pending pickup from ending the first
            // box's interaction); keep them until that is over.
            s_disableWhenIdle = true;
            Log.LogInfo("[PlayerCarry] Disabled: no new second boxes. The mod turns off fully once the second box is back " +
                        "in your hands (or a pending second-box pickup has been answered).");
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
        if (s_disableWhenIdle && !DoubleCarry.HasWork && !Enabled.Value)
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
        DoubleCarry.AbandonStack();
        return true;
    }
}
