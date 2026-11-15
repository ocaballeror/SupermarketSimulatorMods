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
    private static Transform _model;

    /// <summary>True while the local player is on an extra computer (also while it is being opened and closed).</summary>
    internal static bool Active { get; private set; }
    internal static bool InBack;

    internal static bool IsSessionProp(Furniture f) => Active && _prop != null && f != null && f.Pointer == _prop.Pointer;

    /// <summary>Opens the computer UI on <paramref name="prop"/>. Returns what InstantInteract should return.</summary>
    internal static bool TryStart(Furniture prop)
    {
        if (Active) { Plugin.Warn("A computer session is already open"); return false; }

        var comp = ComputerFurniture.FindRealComputer();
        if (comp == null) { Plugin.Warn("The store computer wasn't found; can't open this computer"); return false; }
        if (comp.OpenPC || comp.IsInPlacingMode)
        {
            Plugin.Debug($"Store computer busy locally (open={comp.OpenPC}, being moved={comp.IsInPlacingMode})");
            return false;
        }
        var player = NoktaSingleton<PlayerManager>.HasInstance ? NoktaSingleton<PlayerManager>.Instance.LocalPlayer : null;
        if (player == null || player.PlayerInteraction == null) return false;

        var model = prop.transform.Find(ComputerFurniture.ModelName);
        if (model == null) { Plugin.Warn("This computer has no model; can't open it"); return false; }

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
            End("computer didn't open");
            return false;
        }

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
        if (!Active) return;
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

    /// <summary>Puts the store computer's screen, camera and exit point back.</summary>
    internal static void End(string reason)
    {
        if (!Active) return;
        Active = false;
        InBack = false;
        Presence.SetUse(0);
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

    /// <summary>Leaves the computer properly (as if the player pressed Back), then restores.</summary>
    internal static void ForceEnd(string reason)
    {
        if (!Active) return;
        try
        {
            if (_comp != null && _comp.OpenPC) _comp.Back();
        }
        catch (Exception e) { Plugin.Error("Leaving the computer failed: " + e.Message); }
        End(reason);
    }

    /// <summary>Forgets the session without touching objects (new scene, or after toggling the mod).</summary>
    internal static void Reset()
    {
        if (Active && _comp != null) { End("reset"); }
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
