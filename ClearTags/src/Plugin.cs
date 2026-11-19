using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace ClearTags;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "oscar.cleartags";
    public const string Name = "Clear Tags";
    public const string Version = "0.3.4";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> TagScale;
    internal static ConfigEntry<bool> HoverText;
    internal static ConfigEntry<float> HoverTextSize;
    internal static ConfigEntry<bool> VerboseLogging;

    /// <summary>True while the box patches are applied (the master switch is on).</summary>
    internal static bool Active { get; private set; }

    private static Plugin s_instance;
    private Harmony _harmony;

    public override void Load()
    {
        s_instance = this;
        Logger = Log;
        _harmony = new Harmony(Guid);

        Enabled = Config.Bind("General", "Enabled", true, "Enable the mod.");
        TagScale = Config.Bind("Tags", "ScaleMultiplier", 2f,
            new ConfigDescription(
                "How much bigger the product icon tag on every box is drawn.",
                new ClampedFloat(0.5f, 5f)));
        HoverText = Config.Bind("Hover", "ShowHoverText", true,
            "Show a small panel (product icon and name; on a box also the quantity and the box price) while looking at a label");
        HoverTextSize = Config.Bind("Hover", "TextSize", 30f,
            new ConfigDescription("Size of the hover panel text.",
                new ClampedFloat(8f, 48f)));
        VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", false,
            "Enable verbose logging for debugging purposes.");

        Enabled.SettingChanged += (_, _) => SetActive(Enabled.Value);
        TagScale.SettingChanged += (_, _) => OnScaleChanged();

        AddComponent<ClearTagsBehaviour>();
        SetActive(Enabled.Value, initial: true);
        Log.LogInfo($"{Name} {Version} loaded ({(Active ? "enabled" : "disabled")}, tag scale x{TagScale.Value})");
    }

    private void SetActive(bool on, bool initial = false)
    {
        if (on == Active) return;
        if (on)
        {
            ApplyPatches();
            Active = true;
            if (!initial) TagScaler.ResizeAll("enabled");   // Load() must not touch the game; the hooks handle boxes spawned later
        }
        else
        {
            Active = false;               // TagScaler.Factor is 1 from here on
            _harmony.UnpatchSelf();
            TagScaler.ResizeAll("disabled");
            Log.LogInfo("All patches removed (disabled)");
        }
    }

    private static void OnScaleChanged()
    {
        if (!Active) return;
        TagScaler.ResizeAll("scale changed");
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
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to apply {type.FullName}: {e}");
            }
        }
        Log.LogInfo($"Patches applied ({ok}/{total})");
    }

    internal static void Info(string msg)
    {
        if (VerboseLogging != null && VerboseLogging.Value) Logger.LogInfo("[ClearTags] " + msg);
    }

    public override bool Unload()
    {
        Active = false;
        try { TagScaler.ResizeAll("unload"); } catch { }
        _harmony?.UnpatchSelf();
        return true;
    }
}
