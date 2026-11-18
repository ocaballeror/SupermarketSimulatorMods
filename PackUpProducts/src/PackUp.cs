/*
 * Improvement #14, Pack up products: with empty hands, a right click (the game's Take action) on a product
 * lying on a shelf, fridge or freezer spawns a box of the right size for that product, puts it in your
 * hands, and moves one product from the shelf slot into it.
 *
 * Why nothing happens in vanilla: with empty hands no interaction component is enabled, so nobody listens to
 * the Take action (PlayerInteraction.OnTake only forwards to the active interaction's delegate). The shelf
 * slot (DisplaySlot) is not an IInteractable either: BoxInteraction finds it with its own raycast from the
 * camera (BoxInteraction.CheckForInteraction: Physics.Raycast over m_MaxRaycastDistance / m_BoxInteractionLayer,
 * then the DisplaySlot component on the hit collider's own GameObject).
 *
 * Mechanism (all verified in the native dumps, see CLAUDE.md for the details):
 *  1. A MonoBehaviour polls the Take action of the local player (WasPressedThisFrame). Nothing is patched.
 *  2. Guards: hands really empty (no active Interaction, nothing under the hold point, not in placing mode,
 *     not paused, cursor locked), then the same raycast as BoxInteraction; the hit must be a DisplaySlot that
 *     holds products (ice cream cone/flavour slots are left alone).
 *  3. Right box: the game accepts a product only in a box whose Size equals ProductSO.GridLayoutInBox.boxSize
 *     (BoxInteraction.CheckTakeProductFromSlot) and BoxGenerator.GetBoxBySize picks the prefab for that size,
 *     so the box is spawned with BoxData(size = that boxSize, open, productID, count 0).
 *  4. Host / offline: BoxGenerator.SpawnBox(pos, rot, BoxData), the path InventoryManager.LoadBoxDatas uses for
 *     saved boxes (online it is NetworkObjectGenerator.SpawnNetworkBox = PhotonNetwork.InstantiateRoomObject
 *     with the BoxData as instantiation data, so every client, modded or not, gets a box with that data).
 *     Persistence: loose boxes are saved from InventoryManager.Boxes, which vanilla fills with
 *     InventoryManager.AddBox(box.Data) in Box.Setup(newBox: true). Setup(newBox: true) also resets the count
 *     to a full box (ProductAmountOnPurchase), so we call AddBox ourselves instead.
 *  5. Into the hands: PlayerInteraction.SetCurrentInteractable(box) + Interact(false, false), the same
 *     simulated click the wholesale purchase uses on a freshly spawned box (PurchasableProduct.Purchase). It
 *     runs Box.InstantInteract: offline Box.PickUp(); host NetworkBox.AvailabilityCallback_RPC(own user id)
 *     -> PickUp_Broadcast -> PickUP_RPC to all; then SetCurrentInteraction(BOX) binds BoxInteraction.
 *  6. The product: BoxInteraction.SetCurrentDisplaySlot(slot) + TryTakeProductFromSlot(), the game's own take
 *     (CheckTakeProductFromSlot, then offline TakeProductFromDisplay / host TakeProduct_Broadcast / guest
 *     AskTakeProduct), so counts, animation, inventory bookkeeping and every network message are vanilla.
 *     The box stays open and bound, so further right clicks add more products the normal way.
 *
 * Guest (needs this mod on the host too): a guest can't spawn a master-owned box (SpawnBox returns null for
 * non-masters). It does what a guest buying at the wholesale store does: NetworkObjectGenerator.
 * SpawnNetworkBoxClient (PhotonNetwork.Instantiate; instantiation data carries count 0, CLOSED), then the
 * same simulated click (AskAvailability -> host grants -> PickUP_RPC), then, once the box is really in its
 * hands, the vanilla open (Box.OpenBox -> OpenBox_RPC to All) and the vanilla take (AskTakeProduct -> host
 * relays TakeProduct_RPC). Closed + vanilla open, because NetworkBox.OnPhotonInstantiate does not call
 * Box.set_Data on the master client: the host's copy of a client-spawned box keeps the prefab default data
 * (closed, no product) until Box.AddProduct runs Setup; spawning it open would make the host's
 * TakeProduct_RPC open it again and replay the open animation on the guest. The box is kinematic until held.
 * The one thing vanilla can't do is save the box: its own wholesale route (Setup_Broadcast newBox:true)
 * would reset the count to full. So the guest tells the host to add the box data to InventoryManager.Boxes
 * through a player custom property (Network.cs), and the host sets its copy up first. A guest packs only if
 * the host has published the mod's presence there; a host without the mod would lose the box and the
 * product at the next load.
 *
 * Money: free. The products are already owned and a box from a shelf is not a purchase (wholesale boxes cost
 * money because they bring new products).
 *
 * Failure behaviour: every check happens before anything is spawned. If the game still refuses the take (it
 * shows its own warning), you are left holding an empty open box of the right product, which is a normal
 * game state (put it on a shelf/rack, throw it away, or take products into it).
 */
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PackUpProducts;

internal static class PackUp
{
    private const float ClickCooldown = 0.4f;
    private const float PendingTimeout = 6f;

    private sealed class Pending
    {
        public Box Box;
        public DisplaySlot Slot;
        public float Started;
        public int ViewId;
        public bool Opened;
        public float OpenedAt;
    }

    private static float _nextClick;
    private static Pending _pending;
    private static InputAction _action;
    private static string _actionName;
    private static IntPtr _actionsPtr;
    private static float _nextWarn;

    internal static void Reset()
    {
        _pending = null;
        _action = null;
        _actionsPtr = IntPtr.Zero;
        Network.Reset();
    }

    internal static void Log(string msg) => Plugin.Logger.LogInfo("[PackUp] " + msg);

    internal static void Warn(string msg) => Plugin.Logger.LogWarning("[PackUp] " + msg);

    private static void Vlog(string msg)
    {
        if (Plugin.Verbose.Value) Plugin.Logger.LogInfo("[PackUp] " + msg);
    }

    private static bool Same(Il2CppObjectBase a, Il2CppObjectBase b) => a != null && b != null && a.Pointer == b.Pointer;

    internal static bool InInventory(InventoryManager inv, BoxData data)
    {
        var list = inv.Boxes;
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
        {
            var d = list[i];
            if (d != null && d.Pointer == data.Pointer) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ per frame

    internal static void Update()
    {
        Network.Tick();
        if (!Plugin.Enabled.Value) { Abandon(); return; }

        var p = LocalPlayer();
        if (p == null) { Abandon(); return; }

        if (_pending != null) { TickPending(p); return; }
        if (Time.unscaledTime < _nextClick) return;
        if (!Pressed(p)) return;
        _nextClick = Time.unscaledTime + ClickCooldown;
        TryPack(p);
    }

    private static PlayerInstance LocalPlayer()
    {
        if (!NoktaSingleton<PlayerManager>.HasInstance) return null;
        var pm = NoktaSingleton<PlayerManager>.Instance;
        var p = pm != null ? pm.LocalPlayer : null;
        if (p == null) return null;
        try
        {
            if (!p.IsLocalPlayerInstance) return null;
            if (PhotonNetwork.IsConnected)
            {
                var pv = p.m_PhotonView;
                if (pv == null || !pv.IsMine) return null;
            }
        }
        catch (Exception) { return null; }
        return p;
    }

    private static bool Pressed(PlayerInstance p)
    {
        var name = Plugin.TriggerAction.Value;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var ia = p.InputActions;
        var pin = ia != null ? ia.m_PlayerInput : null;
        var actions = pin != null ? pin.actions : null;
        if (actions == null) return false;
        if (_action == null || _actionName != name || _actionsPtr != actions.Pointer)
        {
            _action = actions.FindAction(name, false);
            _actionName = name;
            _actionsPtr = actions.Pointer;
            if (_action == null)
            {
                if (Time.unscaledTime >= _nextWarn)
                {
                    _nextWarn = Time.unscaledTime + 30f;
                    Warn($"There is no input action called \"{name}\"; nothing will trigger packing (see TriggerAction in the config)");
                }
                return false;
            }
        }
        return _action.WasPressedThisFrame();
    }

    // ------------------------------------------------------------------ the click

    /// <summary>Null when the player's hands and state allow packing, else why not.</summary>
    private static string Busy(PlayerInstance p)
    {
        var pi = p.PlayerInteraction;
        var holder = p.PlayerObjectHolder;
        var bi = p.BoxInteraction;
        if (pi == null || holder == null || bi == null) return "player components missing";
        if (Cursor.lockState == CursorLockMode.None) return "cursor is free (a menu is open)";
        if (pi.m_InInteraction) return "already in an interaction";
        if (pi.m_Paused || pi.m_InGameMenu || pi.m_IsInteractionStoped) return "paused / menu open";
        if (pi.InSkateboard) return "riding a skateboard";
        if (holder.m_PlacingMode) return "placing something";
        if (bi.Enable || bi.IsWaitingAvailability) return "a box is already in your hands";
        var hold = holder.m_ObjectHolder;
        if (hold != null && hold.childCount > 0) return $"hands are not empty ({hold.GetChild(0).name})";
        var active = pi.GetActiveInteracts();
        if (active != null && active.Count > 0) return $"another interaction is active ({active[0].GetIl2CppType().Name})";
        return null;
    }

    private static DisplaySlot FindSlot(BoxInteraction bi)
    {
        var cam = bi.m_MainCamera;
        if (cam == null) cam = Camera.main;
        if (cam == null) return null;
        float dist = bi.m_MaxRaycastDistance;
        if (!(dist > 0.1f)) dist = 3f;
        int mask = bi.m_BoxInteractionLayer.value;
        if (mask == 0) mask = ~0;
        var t = cam.transform;
        if (!Physics.Raycast(new Ray(t.position, t.forward), out RaycastHit hit, dist, mask)) return null;
        var col = hit.collider;
        if (col == null) return null;
        return col.GetComponent<DisplaySlot>();
    }

    private static void TryPack(PlayerInstance p)
    {
        var bi = p.BoxInteraction;
        if (bi == null) return;
        var slot = FindSlot(bi);
        if (slot == null) return; // not aimed at a shelf slot: not our click, stay silent
        string busy = Busy(p);
        if (busy != null) { Vlog($"Right click on a shelf ignored: {busy}"); return; }

        if (slot.TryCast<IceCreamConeDisplaySlot>() != null || slot.TryCast<IceCreamFlavourDisplaySlot>() != null)
        {
            Vlog("Ice cream slot: left to the game");
            return;
        }
        int count = slot.ProductCount;
        int pid = slot.ProductID;
        if (count <= 0 || pid <= 0) { Vlog($"Slot is empty (product {pid}, count {count})"); return; }

        if (!NoktaSingleton<IDManager>.HasInstance || !NoktaSingleton<BoxGenerator>.HasInstance) return;
        var so = NoktaSingleton<IDManager>.Instance.ProductSO(pid);
        var layout = so != null ? so.GridLayoutInBox : null;
        if (so == null || layout == null) { Warn($"No product definition / box layout for product {pid}"); return; }
        var size = layout.boxSize;
        var prefab = NoktaSingleton<BoxGenerator>.Instance.GetBoxBySize(size);
        if (prefab == null) { Warn($"No box prefab for size {size} (product {pid} {so.ProductName})"); return; }

        bool guest = Network.IsGuest;
        if (guest && !Network.HostHasMod())
        {
            if (Time.unscaledTime >= _nextWarn)
            {
                _nextWarn = Time.unscaledTime + 10f;
                Warn("Packing as a guest needs the host to have Pack Up Products too (the host saves the box); nothing done");
            }
            return;
        }

        var holder = p.PlayerObjectHolder;
        var hold = holder.m_ObjectHolder;
        var cam = bi.m_MainCamera != null ? bi.m_MainCamera : Camera.main;
        var pos = hold != null ? hold.position : (cam != null ? cam.transform.position + cam.transform.forward * 0.8f : Vector3.zero);
        float yaw = cam != null ? cam.transform.eulerAngles.y : 0f;
        var rot = Quaternion.Euler(0f, yaw, 0f);

        Log($"Packing {so.ProductName} (product {pid}, {count} on the slot) into a {size} box " +
            $"[{(guest ? "guest" : PhotonNetwork.IsConnected ? "host" : "offline")}]");

        Box box = guest ? SpawnAsGuest(size, pid, pos, rot, out int viewId) : SpawnAsHost(size, pid, pos, rot, out viewId);
        if (box == null) return;

        // The simulated pickup click (what a wholesale purchase does on its new box).
        var pi = p.PlayerInteraction;
        var target = box.TryCast<IInteractable>() ?? new IInteractable(box.Pointer);
        pi.SetCurrentInteractable(target);
        pi.Interact(false, false);

        if (guest)
        {
            _pending = new Pending { Box = box, Slot = slot, Started = Time.unscaledTime, ViewId = viewId };
            Log($"Asked the host for the new box (view {viewId}); waiting for the pickup answer");
            return;
        }
        if (!Same(bi.m_Box, box))
        {
            Warn($"The new box didn't end up in your hands (BoxInteraction.m_Box is {(bi.m_Box != null ? "another box" : "empty")}); " +
                 $"it is lying at {box.transform.position}, pick it up by hand");
            Release(box);
            return;
        }
        TakeInto(p, box, slot);
    }

    // ------------------------------------------------------------------ spawning

    private static Box SpawnAsHost(BoxSize size, int pid, Vector3 pos, Quaternion rot, out int viewId)
    {
        viewId = 0;
        try
        {
            var data = new BoxData(size, pos, rot, true, pid, 0);
            var box = NoktaSingleton<BoxGenerator>.Instance.SpawnBox(pos, rot, data);
            if (box == null) { Warn("BoxGenerator.SpawnBox returned no box"); return null; }
            var d = box.Data;
            if (d == null) { Warn("The spawned box has no data"); return null; }
            var nb = box.NetworkBox;
            if (nb != null) viewId = nb.ViewId;
            var inv = NoktaSingleton<InventoryManager>.Instance;
            if (inv != null && !InInventory(inv, d)) inv.AddBox(d);
            Log($"Spawned the box: uid {d.UID}, view {(nb != null ? viewId.ToString() : "local")}, size {d.Size}, open {d.IsOpen}, " +
                $"product {d.ProductID}, count {d.ProductCount}; added to the saved inventory");
            return box;
        }
        catch (Exception e)
        {
            Warn($"Spawning the box failed: {e}");
            return null;
        }
    }

    private static Box SpawnAsGuest(BoxSize size, int pid, Vector3 pos, Quaternion rot, out int viewId)
    {
        viewId = 0;
        try
        {
            // Closed: the host's copy of a client-spawned box starts closed (see Network.TryRegister), so every
            // client agrees on closed and the vanilla open (Box.OpenBox -> OpenBox_RPC to All) opens it everywhere.
            var data = new BoxData(size, pos, rot, false, pid, 0);
            var box = NoktaSingleton<NetworkObjectGenerator>.Instance.SpawnNetworkBoxClient(pos, rot, data, -1);
            if (box == null) { Warn("NetworkObjectGenerator.SpawnNetworkBoxClient returned no box"); return null; }
            var nb = box.NetworkBox;
            if (nb == null || nb.PhotonView == null) { Warn("The spawned box has no NetworkBox / PhotonView"); return null; }
            viewId = nb.ViewId;
            if (data.UID <= 0) data.m_UID = viewId;   // what BoxGenerator.SpawnBoxLocalMarket does
            box.Data = data;
            // It sits at the hold point, often inside the shelf, for one round trip to the host; with live
            // physics depenetration would shove it (and that is synced). Held boxes are kinematic anyway
            // (HoldObject) and DropObject turns physics back on; Release() covers the failure paths.
            var rb = box.m_RigidBody;
            if (rb != null) rb.isKinematic = true;
            Network.SendRegistration(viewId);
            Log($"Spawned the box as a guest: view {viewId}, size {size}, product {pid}, count {data.ProductCount}; host asked to save it");
            return box;
        }
        catch (Exception e)
        {
            Warn($"Spawning the box failed: {e}");
            return null;
        }
    }

    private static void Abandon()
    {
        if (_pending == null) return;
        Release(_pending.Box);
        _pending = null;
    }

    /// <summary>Give a box we made kinematic back to physics (it isn't going to be held).</summary>
    private static void Release(Box box)
    {
        try
        {
            if (box == null) return;
            // A box that did reach the hands is the game's now (HoldObject made it kinematic, DropObject restores it).
            var lp = LocalPlayer();
            var hold = lp != null && lp.PlayerObjectHolder != null ? lp.PlayerObjectHolder.m_ObjectHolder : null;
            if (hold != null && Same(box.transform.parent, hold)) return;
            var rb = box != null ? box.m_RigidBody : null;
            if (rb != null && (!PhotonNetwork.IsConnected || (box.NetworkBox != null && box.NetworkBox.PhotonView != null && box.NetworkBox.PhotonView.IsMine)))
                rb.isKinematic = false;
        }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ the product

    private static void TakeInto(PlayerInstance p, Box box, DisplaySlot slot)
    {
        var bi = p.BoxInteraction;
        int before = slot.ProductCount;
        bi.SetCurrentDisplaySlot(slot);
        bi.TryTakeProductFromSlot();
        // Host and offline apply the take at once; a guest's answer comes back from the host a moment later.
        Log($"Took a product from the slot: slot {before} -> {slot.ProductCount}, box {box.ProductCount} " +
            $"(product {(box.Product != null ? box.Product.ProductName : "?")}, open {box.IsOpen})");
    }

    private static void TickPending(PlayerInstance p)
    {
        var pd = _pending;
        float age = Time.unscaledTime - pd.Started;
        if (pd.Box == null || pd.Slot == null)
        {
            Warn("The new box or the shelf slot disappeared while waiting; giving up");
            Release(pd.Box);
            _pending = null;
            return;
        }
        var bi = p.BoxInteraction;
        var holder = p.PlayerObjectHolder;
        var hold = holder != null ? holder.m_ObjectHolder : null;
        bool bound = bi != null && Same(bi.m_Box, pd.Box);
        bool held = holder != null && hold != null && Same(holder.m_CurrentObject, pd.Box.gameObject) &&
                    Same(pd.Box.transform.parent, hold);
        if (bound && held && !bi.IsWaitingAvailability)
        {
            if (!pd.Opened)
            {
                // The vanilla open (online Box.OpenBox = NetworkBox.OpenBox_Broadcast, RpcTarget.All, runs here at
                // once and on the host before the take request below): every client opens its copy the same way.
                pd.Opened = true;
                pd.OpenedAt = Time.unscaledTime;
                if (!pd.Box.IsOpen) pd.Box.OpenBox();
                Log("Opened the new box (vanilla open, sent to everyone)");
                return;
            }
            if (!pd.Box.IsOpen && Time.unscaledTime - pd.OpenedAt < 1f) return;
            bi.m_IsBoxOpen = true;   // BoxInteraction's own copy of "open" (normally set after an open click)
            bi.UpdateOpeningBoxHint(); // swaps the "open" key hint for "close", as the vanilla open click does
            _pending = null;
            TakeInto(p, pd.Box, pd.Slot);
            return;
        }
        if (age > 1f && !bound && !bi.Enable)
        {
            Warn($"The host didn't give you the new box (view {pd.ViewId}); it lies on the floor in front of you, pick it up by hand");
            Release(pd.Box);
            _pending = null;
            return;
        }
        if (age > PendingTimeout)
        {
            Warn($"Gave up waiting for the host to hand over the new box (view {pd.ViewId}, bound {bound}, held {held})");
            Release(pd.Box);
            _pending = null;
        }
    }
}
