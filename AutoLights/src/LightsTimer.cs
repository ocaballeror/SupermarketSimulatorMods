using System;
using Il2CppInterop.Runtime;
using Photon.Pun;
using UnityEngine;

namespace AutoLights;

/// <summary>
/// Turns the shop lights on once per in-game day when the clock reaches 19:00.
///
/// Game mechanism (see CLAUDE.md, verified in the dumps):
///  - <c>DayCycleManager</c> holds the clock as a 12-hour value: <c>CurrentHour</c> (+0xA0), <c>CurrentMinute</c> (+0x9C)
///    and <c>AM</c> (+0xA8). The hour counter is set to the day's starting hour when a day begins, and from hour 12
///    on <c>AM</c> is false and hours above 12 have 12 subtracted. The game's own evening lights
///    (<c>MarketLightsController</c>, <c>StreetLights</c>) use the same pair (hour, AM). Every client runs the clock
///    itself; the host also broadcasts a resync at every full hour.
///  - A new day (bed / <c>StartNextDay</c>) resets the clock to the starting hour and fires <c>OnStartedNewDay</c>,
///    which both light managers use to switch their lights off. Sleeping therefore never crosses 19:00.
///  - The shop's lights are the furniture lamps (<c>InteriorSpotLight</c>, <c>OutdoorSpotLight</c>) owned by the
///    singletons <c>StoreLightManager</c> (indoor + outdoor, section "Store") and <c>StorageLightManager</c> (the
///    storage room, section "Storage"). Each has a scene <c>LightSwitch</c> whose <c>m_Section</c> names the manager.
///    <c>LightSwitch.InstantInteract</c> is the player's click: it sets <c>manager.TurnOn = !manager.m_IsOn</c>
///    (online that sends the buffered RPC <c>StoreLightSwitchTurnOn_RPC</c> / <c>StorageLightSwitchTurnOn_RPC</c> to
///    everyone, so guests get the state and late joiners get it through <c>FurnitureManager.SyncCurrentLights_RPC</c>),
///    plays the switch sound and drives the lever animation. The mod calls exactly that on the host.
///  - The light state is not part of the save: the managers' <c>m_IsOn</c> is false after a load.
///
/// Rules implemented here: host (or offline) only; a firing needs the clock to be seen moving forward from before
/// 19:00 to 19:00 or later in a small step (so a jump of the clock does nothing). In the dumps
/// <c>LoadSaveProgress</c> (from <c>DayCycleManager.Awake</c> / <c>NetworkDayCycleManager.Awake</c>) sets hour =
/// <c>m_DayStartingTime</c>, AM true and <c>m_CurrentTimeInFloat</c> 0 and nothing restores hour/minute from the
/// save, so a save loaded "past 19:00" probably cannot happen; the first-sight and step guards are harmless safety; once per <c>CurrentDay</c>; only switches whose manager is off and has lamps; nothing if no switch exists.
/// </summary>
internal static class LightsTimer
{
    private static bool s_havePrev;
    private static int s_prevMinutes;
    private static int s_prevDay;
    private static IntPtr s_prevManager;
    private static int s_firedDay = int.MinValue;

    internal static void Reset()
    {
        s_havePrev = false;
        s_firedDay = int.MinValue;
        s_prevManager = IntPtr.Zero;
    }

    internal static void Tick()
    {
        if (!Plugin.Enabled.Value) { if (s_havePrev) Reset(); return; }
        if (!NoktaSingleton<DayCycleManager>.HasInstance) { if (s_havePrev) Reset(); return; }
        var dcm = NoktaSingleton<DayCycleManager>.Instance;
        if (dcm == null) { Reset(); return; }

        // Guests receive the lights through the game's own sync; only the host (or an offline game) switches.
        if (PhotonNetwork.IsConnected && !PhotonNetwork.IsMasterClient) { if (s_havePrev) Reset(); return; }

        int hour = dcm.CurrentHour;
        int minute = dcm.CurrentMinute;
        bool am = dcm.AM;
        int day = dcm.CurrentDay;
        // 12-hour value + AM flag -> minutes since midnight (hour 12 with AM false is noon; hours past 12 have 12 removed)
        int h24 = am ? (hour == 12 ? 0 : hour) : (hour == 12 ? 12 : hour + 12);
        int now = h24 * 60 + minute;
        IntPtr ptr = dcm.Pointer;

        // First sight of this clock (scene load, host change, re-enabled) or a new day: just remember the time.
        if (!s_havePrev || ptr != s_prevManager || day != s_prevDay)
        {
            s_havePrev = true;
            s_prevManager = ptr;
            s_prevDay = day;
            s_prevMinutes = now;
            Plugin.Vlog($"[AutoLights] Watching the clock: day {day}, {Fmt(now)} (no action)");
            return;
        }

        int prev = s_prevMinutes;
        s_prevMinutes = now;
        int step = now - prev;
        if (step < 0 || step > Plugin.MaxStepMinutes) return;   // clock reset / jumped: not a normal crossing
        if (prev >= Plugin.TriggerMinutes || now < Plugin.TriggerMinutes) return;
        if (s_firedDay == day) return;
        s_firedDay = day;

        Plugin.Vlog($"[AutoLights] Clock crossed 19:00 (day {day}, {Fmt(prev)} -> {Fmt(now)})");
        SwitchOn();
    }

    private static string Fmt(int m) => $"{m / 60:00}:{m % 60:00}";

    private static void SwitchOn()
    {
        var switches = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<LightSwitch>());
        if (switches == null || switches.Length == 0)
        {
            Plugin.Vlog("[AutoLights] No light switch in the scene; nothing to do");
            return;
        }

        int flipped = 0;
        for (int i = 0; i < switches.Length; i++)
        {
            try
            {
                var sw = switches[i]?.TryCast<LightSwitch>();
                if (sw == null) continue;
                string section = sw.m_Section;
                bool off, hasLamps;
                if (section == "Store")
                {
                    if (!NoktaSingleton<StoreLightManager>.HasInstance) continue;
                    var m = NoktaSingleton<StoreLightManager>.Instance;
                    off = !m.TurnOn;
                    hasLamps = Alive(m.m_InteriorLights) + Alive(m.m_OutdoorLights) > 0;
                }
                else if (section == "Storage")
                {
                    if (!NoktaSingleton<StorageLightManager>.HasInstance) continue;
                    var m = NoktaSingleton<StorageLightManager>.Instance;
                    off = !m.TurnOn;
                    hasLamps = Alive(m.m_InteriorLights) > 0;
                }
                else continue;

                if (!off) { Plugin.Vlog($"[AutoLights] {section} lights already on; left alone"); continue; }
                if (!hasLamps) { Plugin.Vlog($"[AutoLights] {section} has no lamps placed; left alone"); continue; }

                sw.InstantInteract();   // the player's own click: sets the manager state, syncs it, plays sound + lever
                flipped++;
                Plugin.Vlog($"[AutoLights] Switched the {section} lights on");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[AutoLights] Switching a light switch failed: {e.Message}");
            }
        }
        if (flipped > 0) Plugin.Logger.LogInfo($"[AutoLights] 19:00: switched on {flipped} light switch(es)");
    }

    /// <summary>Number of lamps in the list that are not destroyed (the managers' lists can still hold dead entries).</summary>
    private static int Alive<T>(Il2CppSystem.Collections.Generic.List<T> list) where T : UnityEngine.Object
    {
        if (list == null) return 0;
        int n = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var l = list[i];
            if (l != null) n++;
        }
        return n;
    }
}
