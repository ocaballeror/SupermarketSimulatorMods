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
    public const string Version = "1.0.3";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<Key> CrouchKey;
    internal static ConfigEntry<CrouchPadButton> PadButton;
    internal static ConfigEntry<bool> ToggleMode;
    internal static ConfigEntry<float> SpeedMultiplier;
    internal static ConfigEntry<float> CameraHeight;
    internal static ConfigEntry<float> TransitionTime;
    internal static ConfigEntry<bool> ShrinkCollider;
    internal static ConfigEntry<float> BodyDepth;
    internal static ConfigEntry<float> LeanAngle;
    internal static ConfigEntry<bool> ShareWithOthers;
    internal static ConfigEntry<bool> AnimateOthers;
    internal static ConfigEntry<bool> Verbose;

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

        SpeedMultiplier = 0.5f;
        CameraHeight = 0.55f;
        TransitionTime = 0.2f;
        ShrinkCollider = true;

        BodyDepth = 0.4f
        LeanAngle = 15f;
        ShareWithOthers = true;
        AnimateOthers = true;

        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", true,
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
        if (Verbose.Value) Logger.LogInfo("[Crouch] " + msg);
    }

    public override bool Unload()
    {
        SetActive(false);
        return true;
    }
}
