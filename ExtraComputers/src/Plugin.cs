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

        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch. Turning it off removes all patches immediately (no restart needed): the computer leaves the " +
            "shop, and placed computers stay in the store as furniture but can't be used until it is turned back on. " +
            "If a store is LOADED while this is off (game started with it off, turned off before loading, or the mod " +
            "removed), a placed extra computer stops the game from loading the furniture saved after it, and computer " +
            "boxes (unopened, in storage, in a pending delivery or in the cart) can't be loaded and may cause errors or be " +
            "lost. Before doing that, box up EVERY placed computer and sell or throw away the boxes, and empty the cart.");
        Price = Config.Bind("ExtraComputers", "Price", 1000f,
            // ClampedFloat instead of AcceptableValueRange: config managers draw a text field, not a slider.
            new ConfigDescription("Price of one extra computer in the furniture shop (0 to 100000). In multiplayer the " +
                                  "host's price is used by everybody.",
                new ClampedFloat(0f, 100000f)));
        IndependentScreens = Config.Bind("ExtraComputers", "IndependentScreens", true,
            "Every computer remembers which apps and tabs were open on it. What you left open on one computer does not " +
            "show up on another; a computer you haven't used yet starts on the desktop. Turn this off to share one " +
            "screen between all computers (the old behaviour, where the last open app follows you).");
        ProtectUnmodded = Config.Bind("Multiplayer", "ProtectUnmoddedPlayers", true,
            "While any other player in the room does not have this mod, the computer is hidden in the shop, buying it is " +
            "refused and computer boxes can't be opened, because a player without the mod gets errors from computer boxes " +
            "and computers. The host's setting decides for purchases and for boxes opened by anyone; a guest with the mod " +
            "also hides the shop item and keeps its own computer boxes closed according to its own setting. " +
            "Turn this off only if everybody has the mod.");
        Icon = Config.Bind("ExtraComputers", "Icon", IconStyle.Model,
            "Picture of the computer in the furniture shop and on its box. Model: a picture made from the computer's 3D " +
            "model, in the style of the other furniture. GameSymbol: the game's flat computer symbol (icon_device_63). " +
            "The shop picks up a change after the store is loaded again.");
        Verbose = Config.Bind("Diagnostics", "VerboseLogging", true,
            "Log how the computer furniture is built and every time an extra computer is used " +
            "(tag [ExtraComputers] in LogOutput.log).");

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
