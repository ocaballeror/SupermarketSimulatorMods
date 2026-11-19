/*
 * ExtraComputers: using an extra computer.
 *
 * How the store computer works (verified in the native dumps):
 *  - Computer.InstantInteract: refuses (warning popup) if IsOccupied (another player is on it).
 *    Otherwise it moves m_PCCam to its default local position, set_OpenPC(true) (activates the
 *    m_PCCam camera that looks at the world-space UI canvases under m_ComputerScreen, shows the
 *    cursor, ...), freezes the player, sets OccupiedPlayer = local player and, online, calls
 *    NetworkComputer.InteractComputer_Broadcast(true) and puts the player's avatar in front of m_PCCam.
 *    PlayerInteraction then activates ComputerInteraction (type COMPUTER) for the computer.
 *  - Leaving: ComputerInteraction.OnCancel / GamepadBack / WholeSaleManager -> Computer.Back():
 *    set_OpenPC(false) (which broadcasts InteractComputer(false) online), then puts the player at
 *    m_PlayerPosition and broadcasts again.
 *  - InteractComputer_RPC on the other clients only sets IsOccupied / OccupiedPlayer.
 *  - There is exactly one Computer (with its UI, cart, pricing viewer, ...) and several managers and
 *    singletons reference it, so it can't be cloned.
 *
 * What a session on an extra computer does:
 *  - Each game client has its own copy of the computer UI and only one local player, so a player
 *    can borrow their own client's UI. For the session, m_ComputerScreen, m_PCCam and
 *    m_PlayerPosition are moved under "anchor" objects that sit where those parts' parents would be if
 *    the store computer stood where the extra computer stands, then the vanilla
 *    Computer.InstantInteract / Back run unchanged. Afterwards the parts go back exactly where they were.
 *  - The anchors follow the extra computer every frame (Computer.LateUpdate) and are not its
 *    children, so the real UI survives if someone else boxes up that extra computer meanwhile.
 *  - The occupied broadcasts are skipped during the session, so the store computer stays free for the
 *    other players; if another player is on the store computer, IsOccupied is bypassed for the call
 *    and restored afterwards. Changes made on any computer go through the game's normal networked
 *    code, exactly as on the store computer; two people on screens of the same tab may each see an
 *    older view until they reopen it (same as vanilla).
 *  - IsOccupied and OccupiedPlayer of the store computer are saved before and restored after the vanilla
 *    InstantInteract unconditionally, so the store computer never looks used because of an extra one.
 *  - The extra computer in use is published as a player property (Presence.SetUse); a second player who tries
 *    to open the same computer gets the game's "in use by another player" warning.
 *
 * Leaving (the camera):
 *  - m_PCCam is a CinemachineVirtualCamera ("Computer VCam"), not a plain camera. Back() deactivates it, and the
 *    CinemachineBrain then blends from it back to the player's first-person virtual camera. For that blend
 *    CinemachineBlend keeps updating the OUTGOING virtual camera every frame from its own transform, even though it is
 *    inactive. If the borrowed parts are put back under the store computer right after Back (as v1.3.0 did), the outgoing
 *    camera jumps to the store computer and the blend starts there. So after Back the session only becomes "lingering":
 *    the anchors keep following the extra computer and the parts stay where they are until the brain has finished
 *    blending (TickLinger, from Computer.LateUpdate), a new session starts, the scene changes or the mod is turned off.
 *  - Entering is not affected: the camera is moved while inactive and then activated, and the brain blends from the
 *    player's camera to its current pose.
 *
 * Screens: each computer keeps its own open apps and tabs, see ComputerUiState (captured when Back/close runs,
 * applied just before the vanilla InstantInteract).
 */
using System;
using System.Collections.Generic;
using __Project__.Scripts.Computer;
using Photon.Pun;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ExtraComputers;

internal static class ComputerSession
{
    private sealed class Moved
    {
        public Transform Part, OrigParent, Anchor;
        public bool HadParent;
        public int Sibling;
        public Vector3 LocalPos, LocalScale;
        public Quaternion LocalRot;
        public Vector3 RelPos, RelScale;
        public Quaternion RelRot;
    }

    private static readonly List<Moved> MovedParts = new();
    private static readonly List<Renderer> HiddenRenderers = new();
    private static Computer _comp;
    private static Transform _compRoot;
    private static Furniture _prop;
    private static int _screenKey = ComputerUiState.StoreKey;
    private static Transform _model;

    /// <summary>True while the local player is on an extra computer (also while it is being opened and closed).</summary>
    internal static bool Active { get; private set; }
    internal static bool InBack;

    /// <summary>The session is over but the borrowed parts stay on the extra computer until the exit blend is finished.</summary>
    internal static bool Lingering { get; private set; }
    private static float _lingerStart;
    private static int _lingerFrames;
    private const float MinLingerSeconds = 0.2f;
    private const float MaxLingerSeconds = 4f;
    private static Cinemachine.CinemachineBrain _brain;

    /// <summary>True between a vanilla (store computer) InstantInteract that opened the PC and its Back.</summary>
    internal static bool StoreSessionOpen;

    internal static bool IsSessionProp(Furniture f) => Active && _prop != null && f != null && f.Pointer == _prop.Pointer;

    /// <summary>Opens the computer UI on <paramref name="prop"/>. Returns what InstantInteract should return.</summary>
    internal static bool TryStart(Furniture prop)
    {
        if (Active) { Plugin.Warn("A computer session is already open"); return false; }
        if (Lingering) FinishLinger("new session");

        var comp = ComputerFurniture.FindRealComputer();
        if (comp == null) { Plugin.Warn("The store computer wasn't found; can't open this computer"); return false; }
        if (comp.OpenPC || comp.IsInPlacingMode)
        {
            Plugin.Debug($"Store computer busy locally (open={comp.OpenPC}, being moved={comp.IsInPlacingMode})");
            return false;
        }
        var player = NoktaSingleton<PlayerManager>.HasInstance ? NoktaSingleton<PlayerManager>.Instance.LocalPlayer : null;
        if (player == null || player.PlayerInteraction == null) return false;

        // The furniture is a copy of the store computer (same layout as its root), see ComputerFurniture.
        var model = prop.transform;

        // Another player on this very computer: their avatar would overlap ours (the store computer prevents that
        // with IsOccupied; extra computers use a player property, see Presence).
        var view = prop.GetComponent<PhotonView>();
        int viewId = view != null ? view.ViewID : 0;
        if (Presence.IsInUseByOther(viewId))
        {
            Plugin.Debug($"Computer view {viewId} is in use by another player");
            RaiseInUseWarning();
            return false;
        }
        InBack = false;

        var pm = comp.m_PlacingMode;
        _compRoot = pm != null && pm.m_Furniture != null ? pm.m_Furniture.transform : comp.transform;
        _comp = comp;
        _prop = prop;
        _model = model;
        _screenKey = ComputerUiState.KeyOf(prop); // fixed for the whole session (the object may be destroyed meanwhile)
        Active = true;

        try
        {
            Relocate(comp.m_ComputerScreen != null ? comp.m_ComputerScreen.transform : null);
            Relocate(comp.m_PCCam != null ? comp.m_PCCam.transform : null);
            Relocate(comp.m_PlayerPosition);
            SyncAnchors();
            HideOwnScreen(prop);
        }
        catch (Exception e)
        {
            Plugin.Error("Moving the computer screen failed: " + e);
            End("setup failed");
            return false;
        }

        // This computer's own screen (apps and tabs it was left with, or the clean desktop).
        try { ComputerUiState.Apply(comp, _screenKey); }
        catch (Exception e) { Plugin.Error("Setting up this computer's screen failed: " + e); }

        // Vanilla InstantInteract writes IsOccupied-related state (OccupiedPlayer at +0xD8) for the local player;
        // nobody clears it for an extra computer (the broadcast that would is skipped), so put both back.
        bool wasOccupied = comp.IsOccupied;
        var occupant = comp.OccupiedPlayer;
        bool result;
        try
        {
            comp.IsOccupied = false;
            result = comp.InstantInteract();
        }
        catch (Exception e)
        {
            Plugin.Error("Opening the computer failed: " + e);
            result = false;
        }
        finally
        {
            comp.IsOccupied = wasOccupied;
            comp.OccupiedPlayer = occupant;
        }

        if (!result)
        {
            ComputerUiState.DropPending();
            End("computer didn't open");
            return false;
        }

        // The windows this computer had open go up now, like a click on the desktop icons with the PC already open.
        try { ComputerUiState.Finish(comp); }
        catch (Exception e) { Plugin.Error("Opening this computer's windows failed: " + e); }

        // PlayerInteraction.Interact activates the interaction of m_CurrentInteractable's type right after
        // this returns; point it at the store computer so ComputerInteraction takes over as usual.
        player.PlayerInteraction.m_CurrentInteractable = new IInteractable(comp.Pointer);
        Presence.SetUse(viewId);
        Plugin.Debug($"Opened extra computer at {prop.transform.position}" +
                     (wasOccupied ? " (the store computer is in use by another player)" : ""));
        return true;
    }

    private static void Relocate(Transform part)
    {
        if (part == null) return;
        foreach (var m in MovedParts)
            if (part.IsChildOf(m.Part)) return; // moves along with an already moved parent

        var p = part.parent;
        var moved = new Moved
        {
            Part = part,
            OrigParent = p,
            HadParent = p != null,
            Sibling = part.GetSiblingIndex(),
            LocalPos = part.localPosition,
            LocalRot = part.localRotation,
            LocalScale = part.localScale,
        };
        // Pose of the part's parent relative to the store computer's root.
        var pPos = p != null ? p.position : Vector3.zero;
        var pRot = p != null ? p.rotation : Quaternion.identity;
        var pScale = p != null ? p.lossyScale : Vector3.one;
        moved.RelPos = _compRoot.InverseTransformPoint(pPos);
        moved.RelRot = Quaternion.Inverse(_compRoot.rotation) * pRot;
        moved.RelScale = ComputerFurniture.Div(pScale, _compRoot.lossyScale);

        moved.Anchor = new GameObject("ExtraComputers.Anchor." + part.name).transform;
        MovedParts.Add(moved);
        SyncAnchor(moved);
        part.SetParent(moved.Anchor, false);
    }

    private static void SyncAnchor(Moved m)
    {
        if (_model == null || m.Anchor == null) return;
        m.Anchor.SetPositionAndRotation(_model.TransformPoint(m.RelPos), _model.rotation * m.RelRot);
        m.Anchor.localScale = Vector3.Scale(_model.lossyScale, m.RelScale);
    }

    /// <summary>Keeps the borrowed parts on the extra computer if it moves (called every frame).</summary>
    internal static void SyncAnchors()
    {
        if (!Active && !Lingering) return;
        foreach (var m in MovedParts) SyncAnchor(m);
    }

    private static void HideOwnScreen(Furniture prop)
    {
        var rends = prop.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rends.Length; i++)
        {
            var r = rends[i];
            if (r == null || !r.enabled || !r.gameObject.name.EndsWith(ComputerFurniture.ScreenMarker)) continue;
            r.enabled = false;
            HiddenRenderers.Add(r);
        }
    }

    private static void RaiseInUseWarning()
    {
        try
        {
            if (NoktaSingletonPunCallbacks<WarningSystem>.HasInstance)
                NoktaSingletonPunCallbacks<WarningSystem>.Instance.RaiseInteractionWarning(InteractionWarningType.IN_USE_BY_OTHER_PLAYER);
        }
        catch (Exception e) { Plugin.Debug("Showing the in-use warning failed: " + e.Message); }
    }

    /// <summary>Called from the Back postfix and the set_OpenPC(false) postfix: saves this computer's screen, then ends.</summary>
    internal static void AfterClose(Computer comp, string reason)
    {
        if (Active)
        {
            SaveScreen(_comp != null ? _comp : comp, _screenKey);
            End(reason, linger: true);
        }
        else if (StoreSessionOpen)
        {
            StoreSessionOpen = false;
            SaveScreen(comp, ComputerUiState.StoreKey);
        }
    }

    private static void SaveScreen(Computer comp, int key)
    {
        if (comp == null) return;
        try { ComputerUiState.Capture(comp, key); }
        catch (Exception e) { Plugin.Error("Saving this computer's screen failed: " + e.Message); }
    }

    /// <summary>
    /// Ends the session. With <paramref name="linger"/> the borrowed parts stay on the extra computer (see the header)
    /// until the camera blend is done; otherwise they go back at once.
    /// </summary>
    internal static void End(string reason, bool linger = false)
    {
        if (!Active) return;
        Active = false;
        InBack = false;
        Presence.SetUse(0);
        if (linger && MovedParts.Count > 0)
        {
            Lingering = true;
            _lingerStart = Time.unscaledTime;
            _lingerFrames = 0;
            Plugin.Debug("Left extra computer (" + reason + "); keeping the camera on it until the exit blend is done");
            return;
        }
        RestoreParts(reason);
    }

    /// <summary>Called every frame (Computer.LateUpdate): puts the parts back once the exit blend is over.</summary>
    internal static void TickLinger()
    {
        if (!Lingering) return;
        _lingerFrames++;
        float t = Time.unscaledTime - _lingerStart;
        bool done = t >= MaxLingerSeconds;
        if (!done && _lingerFrames >= 3 && t >= MinLingerSeconds)
        {
            try
            {
                if (_brain == null)
                {
                    var cam = Camera.main;
                    _brain = cam != null ? cam.GetComponent<Cinemachine.CinemachineBrain>() : null;
                    if (_brain == null) _brain = Object.FindObjectOfType<Cinemachine.CinemachineBrain>();
                }
                done = _brain == null || !_brain.IsBlending;
            }
            catch (Exception e)
            {
                Plugin.Warn("Checking the camera blend failed: " + e.Message);
                done = true;
            }
        }
        if (done) FinishLinger($"exit blend done after {t:0.00}s");
    }

    internal static void FinishLinger(string reason)
    {
        if (!Lingering) return;
        RestoreParts(reason);
    }

    /// <summary>Puts the store computer's screen, camera and exit point back.</summary>
    private static void RestoreParts(string reason)
    {
        Lingering = false;
        for (int i = MovedParts.Count - 1; i >= 0; i--)
        {
            var m = MovedParts[i];
            try
            {
                if (m.Part != null)
                {
                    var parent = m.HadParent ? (m.OrigParent != null ? m.OrigParent : _compRoot) : null;
                    m.Part.SetParent(parent, false);
                    m.Part.localPosition = m.LocalPos;
                    m.Part.localRotation = m.LocalRot;
                    m.Part.localScale = m.LocalScale;
                    if (parent != null && m.Sibling < parent.childCount) m.Part.SetSiblingIndex(m.Sibling);
                }
            }
            catch (Exception e) { Plugin.Error($"Putting back '{m.Part?.name}' failed: {e.Message}"); }
            if (m.Anchor != null) Object.Destroy(m.Anchor.gameObject);
        }
        MovedParts.Clear();
        foreach (var r in HiddenRenderers)
            if (r != null) r.enabled = true;
        HiddenRenderers.Clear();
        _prop = null;
        _model = null;
        Plugin.Debug("Closed extra computer (" + reason + ")");
    }

    /// <summary>
    /// Leaves the computer the way Escape does, then restores. Must run while the patches are still applied (the
    /// Back postfix ends the session, and the skipped occupied broadcast keeps the store computer free for others).
    /// ComputerInteraction.OnCancel (ISIL) is Back, then PlayerController.EnableController(true, true), then
    /// PlayerInteraction.InteractionEnd; calling only Back would leave the player frozen in the computer interaction.
    /// </summary>
    internal static void ForceEnd(string reason)
    {
        if (!Active) { FinishLinger(reason); return; }
        try
        {
            var player = NoktaSingleton<PlayerManager>.HasInstance ? NoktaSingleton<PlayerManager>.Instance.LocalPlayer : null;
            var ci = player != null ? player.ComputerInteraction : null;
            bool inComputerInteraction = ci != null && ci.enabled && ci.m_Computer != null && _comp != null &&
                                         ci.m_Computer.Pointer == _comp.Pointer;
            if (inComputerInteraction && !ci.m_PlacingMode && !ci.isInDoorCamera)
            {
                ci.OnCancel();
            }
            else
            {
                // OnCancel would do nothing in these states; do its steps directly.
                if (_comp != null && _comp.OpenPC) _comp.Back();
                if (inComputerInteraction)
                {
                    player.PlayerController?.EnableController(true, true, false);
                    player.PlayerInteraction?.InteractionEnd(ci);
                }
            }
        }
        catch (Exception e) { Plugin.Error("Leaving the computer failed: " + e.Message); }
        End(reason);
        FinishLinger(reason); // the mod is going away: no waiting for the blend
    }

    /// <summary>Forgets the session without touching objects (new scene, or after toggling the mod).</summary>
    internal static void Reset()
    {
        if (Active && _comp != null) { End("reset"); }
        if (Lingering) RestoreParts("reset");
        Lingering = false;
        StoreSessionOpen = false;
        _brain = null;
        ComputerUiState.Clear();
        Active = false;
        InBack = false;
        Presence.SetUse(0);
        MovedParts.Clear();
        HiddenRenderers.Clear();
        _prop = null;
        _model = null;
        _comp = null;
        _compRoot = null;
    }
}
