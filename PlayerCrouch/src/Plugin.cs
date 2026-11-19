using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace PlayerCrouch;

/// <summary>Gamepad buttons that can be used for crouching (None = keyboard only).</summary>
public enum CrouchPadButton
{
    None,
    LeftStickPress,
    RightStickPress,
    ButtonNorth,
    ButtonSouth,
    ButtonEast,
    ButtonWest,
    LeftShoulder,
    RightShoulder,
    DpadUp,
    DpadDown,
    DpadLeft,
    DpadRight,
}

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.playercrouch";
    public const string Name = "Player Crouch";
    public const string Version = "1.0.4";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<Key> CrouchKey;
    internal static ConfigEntry<CrouchPadButton> PadButton;
    internal static ConfigEntry<bool> ToggleMode;
    internal static ConfigEntry<bool> VerboseLogging;

    // Fixed tuning values (not exposed as settings).
    /// <summary>Walk speed multiplier while crouched.</summary>
    internal const float SpeedMultiplier = 0.5f;
    /// <summary>Crouched camera/body height as a fraction of standing height.</summary>
    internal const float CameraHeight = 0.55f;
    /// <summary>Seconds to go from standing to fully crouched (and back).</summary>
    internal const float TransitionTime = 0.2f;
    /// <summary>Shrink the CharacterController while crouched so the player fits under low obstacles.</summary>
    internal const bool ShrinkCollider = true;
    /// <summary>How far the body of other players drops when crouching, as a fraction of leg length.</summary>
    internal const float BodyDepth = 0.4f;
    /// <summary>Forward lean of other players' bodies when crouching, in degrees.</summary>
    internal const float LeanAngle = 15f;
    /// <summary>Tell other players (via a player property) that the local player is crouching.</summary>
    internal const bool ShareWithOthers = true;
    /// <summary>Animate other players that share their crouch state.</summary>
    internal const bool AnimateOthers = true;

    internal static bool Active { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");

        CrouchKey = Config.Bind("Controls", "Key", Key.LeftCtrl,
            "Key binding used to crouch.");
        PadButton = Config.Bind("Controls", "GamepadButton", CrouchPadButton.None,
            "Gamepad button used to crouch. None = no gamepad button.");
        ToggleMode = Config.Bind("Controls", "ToggleMode", false,
            "false = hold the key to stay crouched. true = press once to crouch, again to stand up.");

        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Enable verbose logging for debugging purposes.");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);
        AddComponent<CrouchBehaviour>();

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
        Crouch.Reset();
        CrouchPose.Reset();
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

    internal static void Info(string msg)
    {
        if (VerboseLogging.Value) Logger.LogInfo("[Crouch] " + msg);
    }

    public override bool Unload()
    {
        SetActive(false);
        return true;
    }
}
