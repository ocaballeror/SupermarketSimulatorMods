using System;
using __Project__.Scripts.Computer;
using __Project__.Scripts.Multiplayer;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Lean.Pool;
using Photon.Pun;
using UnityEngine;

namespace ExtraComputers;

// ------------------------------------------------------------------ furniture definition

/// <summary>New game scene: forget the old scene's objects, register the computer furniture.</summary>
[HarmonyPatch(typeof(IDManager), nameof(IDManager.Awake))]
internal static class IDManagerAwakePatch
{
    private static void Postfix(IDManager __instance)
    {
        try
        {
            ComputerSession.Reset();
            ComputerFurniture.OnSceneObjectsGone();
            ComputerFurniture.EnsureSo(__instance);
            ComputerFurniture.EnsureTemplate(__instance);
        }
        catch (Exception e) { Plugin.Error("Setup on scene load failed: " + e); }
    }
}

/// <summary>Every furniture lookup by id (cart, delivery, box, spawner, box-up) ends here.</summary>
[HarmonyPatch(typeof(IDManager), nameof(IDManager.FurnitureSO))]
internal static class IDManagerFurnitureSOPatch
{
    private static bool Prefix(IDManager __instance, int id, ref FurnitureSO __result)
    {
        if (id != ComputerFurniture.FurnitureId) return true;
        try
        {
            var so = ComputerFurniture.EnsureSo(__instance);
            if (so == null) return true;
            __result = so;
            return false;
        }
        catch (Exception e) { Plugin.Error("Furniture lookup failed: " + e.Message); return true; }
    }
}

/// <summary>A saved shopping cart is validated against the furniture list on load.</summary>
[HarmonyPatch(typeof(CartManager), nameof(CartManager.Initialize))]
internal static class CartManagerInitializePatch
{
    private static void Prefix()
    {
        try { ComputerFurniture.EnsureSo(); }
        catch (Exception e) { Plugin.Error("Cart setup failed: " + e.Message); }
    }
}

/// <summary>The furniture shop builds its items from IDManager.m_Furnitures.</summary>
[HarmonyPatch(typeof(FurnituresViewer), nameof(FurnituresViewer.SpawnFurnitures))]
internal static class FurnituresViewerSpawnPatch
{
    private static void Prefix()
    {
        try
        {
            ComputerFurniture.EnsureSo();
            ComputerFurniture.EnsureIcon();
        }
        catch (Exception e) { Plugin.Error("Shop setup failed: " + e); }
    }
}

/// <summary>Remembers the computer's shop entry, so it can be hidden (mod off, or a player without the mod).</summary>
[HarmonyPatch(typeof(FurnitureSalesItem), nameof(FurnitureSalesItem.Setup))]
internal static class ShopItemPatch
{
    private static void Postfix(FurnitureSalesItem __instance, int productID)
    {
        if (productID != ComputerFurniture.FurnitureId) return;
        try
        {
            ComputerFurniture.ShopItem = __instance;
            Presence.Blocked(); // refresh the cached state
            ComputerFurniture.RefreshShopItem();
        }
        catch (Exception e) { Plugin.Error("Shop item setup failed: " + e.Message); }
    }
}

// ------------------------------------------------------------------ protecting players without the mod (see Presence)

/// <summary>
/// Host/offline purchase (a guest's purchase request ends up here on the host too). Refused while a player
/// without the mod is in the room and the cart holds a computer: the delivery would create computer boxes.
/// </summary>
[HarmonyPatch(typeof(MarketShoppingCart), nameof(MarketShoppingCart.Purchase))]
internal static class PurchaseGatePatch
{
    private static bool Prefix(MarketShoppingCart __instance)
    {
        try
        {
            if (PhotonNetwork.IsConnectedAndSlave) return true; // guests only send a request to the host
            if (!CartHasComputer(__instance)) return true;
            if (!ComputerFurniture.EnsureTemplate())
            {
                // Purchase reads FurnitureSO.FurniturePrefab for every furniture in the cart after the delivery has
                // been created and throws on a null prefab (ISIL), and the box could never be opened anyway.
                Plugin.Error("Purchase refused: the cart holds a computer, but the computer furniture couldn't be built " +
                             "(see 'Building the computer furniture failed' earlier in this log). Remove it from the cart.");
                return false;
            }
            if (!Presence.Blocked(out var who)) return true;
            Plugin.Warn($"Purchase refused: the cart holds a computer and {who} doesn't have the Extra Computers mod. " +
                        "Remove the computer from the cart (or turn Multiplayer/ProtectUnmoddedPlayers off).");
            return false;
        }
        catch (Exception e)
        {
            Plugin.Error("Purchase check failed: " + e.Message);
            return true;
        }
    }

    private static bool CartHasComputer(MarketShoppingCart cart)
    {
        var data = cart != null ? cart.CartData : null;
        var list = data?.FurnituresInCarts;
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
        {
            var q = list[i];
            if (q != null && q.FirstItemID == ComputerFurniture.FurnitureId) return true;
        }
        return false;
    }
}

/// <summary>
/// Opening a furniture box online runs FurnitureBoxInteraction.NetworkOpenBox on the host, for both paths: the
/// host's own click (OnUse -> OpenFurnitureBox_Broadcast -> NetworkOpenBox) and a guest's request
/// (OpenFurnitureBoxRequest -> RPC -> OpenFurnitureBoxRequest_RPC -> NetworkOpenBox, which does not pass
/// through the broadcast method; verified in the ISIL). A computer box stays closed while a player without the
/// mod is in the room, because unboxing makes the host instantiate a prefab that player can't load.
/// Offline, OnUse calls OpenBox directly, and Presence.Blocked is false offline anyway.
/// </summary>
[HarmonyPatch(typeof(FurnitureBoxInteraction), nameof(FurnitureBoxInteraction.NetworkOpenBox))]
internal static class OpenBoxGatePatch
{
    private static bool Prefix(FurnitureBoxInteraction __instance) => !OpenBoxGate.Refuse(__instance, "host");
}

/// <summary>
/// The same check on the requesting guest. <c>OpenFurnitureBoxRequest</c> transfers the box's PhotonView ownership
/// to the master before it sends the request RPC (ISIL), so a refusal only on the host would leave the guest holding
/// a box that the host now owns (transform sync would follow the host). The host-side gate above stays authoritative.
/// </summary>
[HarmonyPatch(typeof(NetworkFurnitureBoxInteraction), nameof(NetworkFurnitureBoxInteraction.OpenFurnitureBoxRequest))]
internal static class OpenBoxRequestGatePatch
{
    private static bool Prefix(NetworkFurnitureBoxInteraction __instance) =>
        !OpenBoxGate.Refuse(__instance.FurnitureBoxInteraction, "guest");
}

/// <summary>Offline, OnUse calls OpenBox directly (no NetworkOpenBox).</summary>
[HarmonyPatch(typeof(FurnitureBoxInteraction), nameof(FurnitureBoxInteraction.OpenBox))]
internal static class OpenBoxOfflineGatePatch
{
    private static bool Prefix(FurnitureBoxInteraction __instance) => !OpenBoxGate.Refuse(__instance, "local");
}

internal static class OpenBoxGate
{
    /// <summary>True when this is a computer box and either the computer furniture couldn't be built on this client,
    /// or a player without the mod is in the room.</summary>
    internal static bool Refuse(FurnitureBoxInteraction interaction, string side)
    {
        try
        {
            var data = interaction?.CurrentFurnitureBox?.Data;
            if (data == null || data.FurnitureID != ComputerFurniture.FurnitureId) return false;
            bool hostOnline = PhotonNetwork.IsConnected && PhotonNetwork.IsMasterClient;
            if (!ComputerFurniture.EnsureTemplate() || (hostOnline && !ComputerFurniture.RegisterWithPhoton()))
            {
                // Opening would use up the box and spawn nothing, leaving the player in an empty placing mode.
                Plugin.Error($"Computer box not opened ({side} side): the computer furniture couldn't be built " +
                             "(see the 'Building the computer furniture failed' error earlier in this log).");
                return true;
            }
            if (!Presence.Blocked(out var who)) return false;
            Plugin.Warn($"Computer box not opened ({side} side): {who} doesn't have the Extra Computers mod " +
                        "(or turn Multiplayer/ProtectUnmoddedPlayers off). Keep the box until everybody has the mod.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Error("Box check failed: " + e.Message);
            return false;
        }
    }
}

[HarmonyPatch(typeof(LocalizationManager), nameof(LocalizationManager.LocalizedFurnitureName))]
internal static class FurnitureNamePatch
{
    private static bool Prefix(int furnitureID, ref string __result)
    {
        if (furnitureID != ComputerFurniture.FurnitureId) return true;
        __result = ComputerFurniture.DisplayName;
        return false;
    }
}

/// <summary>The icon printed on the furniture box.</summary>
[HarmonyPatch(typeof(ProductAtlasManager), nameof(ProductAtlasManager.SetFurnitureIcon))]
internal static class BoxIconPatch
{
    private static bool Prefix(int furnitureID, MeshRenderer mRend)
    {
        if (furnitureID != ComputerFurniture.FurnitureId) return true;
        try { return !ComputerFurniture.ApplyBoxIcon(mRend); }
        catch (Exception e) { Plugin.Error("Box icon failed: " + e.Message); return true; }
    }
}

// ------------------------------------------------------------------ spawning

internal static class ComputerSpawner
{
    internal static GameObject Spawn(Vector3 pos, Quaternion rot, Transform parent, int boxViewId)
    {
        ComputerFurniture.EnsureSo();
        if (!ComputerFurniture.EnsureTemplate())
        {
            Plugin.Error("Can't spawn a computer: its furniture prefab couldn't be built (see earlier errors)");
            return null;
        }
        if (!PhotonNetwork.IsConnected)
        {
            Plugin.Debug($"Spawning computer at {pos} (offline)");
            var spawned = LeanPool.Spawn(ComputerFurniture.Template, pos, rot, parent);
            // LeanPool re-uses despawned objects: a "new" computer must not inherit an old one's screen.
            if (spawned != null) ComputerUiState.Forget(spawned.GetInstanceID());
            return spawned;
        }
        if (!PhotonNetwork.IsMasterClient)
        {
            Plugin.Warn("A guest was asked to spawn a computer; only the host spawns furniture");
            return null;
        }
        if (Presence.Blocked(out var who))
            Plugin.Warn($"Spawning a computer although {who} doesn't have the mod; that player will get errors for it.");
        ComputerFurniture.RegisterWithPhoton();
        var data = new Il2CppReferenceArray<Il2CppSystem.Object>(1);
        unsafe
        {
            int v = boxViewId;
            data[0] = new Il2CppSystem.Object(IL2CPP.il2cpp_value_box(Il2CppClassPointerStore<int>.NativeClassPtr, (IntPtr)(&v)));
        }
        Plugin.Debug($"Spawning computer at {pos} (online, box view {boxViewId})");
        var room = PhotonNetwork.InstantiateRoomObject(ComputerFurniture.PrefabId, pos, rot, 0, data);
        if (room != null) ComputerUiState.Forget(room.GetInstanceID());
        return room;
    }

    /// <summary>
    /// Stand-in for a saved computer that couldn't be spawned: an inactive object with a <c>Furniture</c> of our id.
    /// LoadFurnitureDatas only needs GetComponent&lt;Furniture&gt;() and set_Data on it, so the rest of the store
    /// loads. It stays inactive, so Furniture.OnEnable never subscribes its OnSave (which only writes the pose back
    /// into the data), and the computer's entry stays in FurnitureManager.m_FurnituresDatas unchanged: the computer
    /// is still in the next save and comes back once the mod can build it again.
    /// </summary>
    internal static GameObject Placeholder(Vector3 pos, Quaternion rot, Transform parent)
    {
        var go = new GameObject("ExtraComputers.SavedComputerStandIn");
        go.SetActive(false);
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(pos, rot);
        var f = go.AddComponent<Furniture>();
        f.m_ID = ComputerFurniture.FurnitureId;
        Plugin.Error($"A saved computer at {pos} couldn't be spawned; it is kept in the save but not shown this session.");
        return go;
    }
}

/// <summary>Unboxing: the computer arrives with its user side (where the PC camera and the exit point are) towards the
/// player. Vanilla turns a furniture's +Z towards the player, but the computer's user side is its -Z.</summary>
[HarmonyPatch(typeof(FurnitureGenerator), nameof(FurnitureGenerator.SpawnFurniture),
    new[] { typeof(int), typeof(Vector3), typeof(int) })]
internal static class SpawnFromBoxPatch
{
    private static bool Prefix(FurnitureGenerator __instance, int id, Vector3 position, int furnitureBoxInteractionViewId,
        ref GameObject __result)
    {
        if (id != ComputerFurniture.FurnitureId) return true;
        try
        {
            var rot = Quaternion.identity;
            var player = NoktaSingleton<PlayerManager>.HasInstance ? NoktaSingleton<PlayerManager>.Instance.LocalPlayer : null;
            if (player != null)
            {
                var fwd = player.transform.forward;
                fwd.y = 0;
                if (fwd.sqrMagnitude > 1e-6f)
                {
                    // Turn the user side (local, horizontal) to point from the computer towards the player (-fwd).
                    var front = ComputerFurniture.UserSideLocal();
                    rot = Quaternion.LookRotation(-fwd.normalized, Vector3.up) *
                          Quaternion.Inverse(Quaternion.LookRotation(front, Vector3.up));
                }
            }
            __result = ComputerSpawner.Spawn(position, rot, __instance.transform, furnitureBoxInteractionViewId);
        }
        catch (Exception e) { Plugin.Error("Spawning a computer failed: " + e); __result = null; }
        return false;
    }
}

/// <summary>
/// Loading a save. This must never return null: FurnitureManager.LoadFurnitureDatas logs "Can't spawn furniture
/// with id" for a null spawn and then dereferences it anyway (ISIL: the null test right after the LogError jumps
/// to the NRE thrower; the loop only has a try/finally). The exception ends the whole load, so every furniture
/// saved after the computer would be missing. A failed spawn returns an inactive placeholder instead (see
/// <see cref="ComputerSpawner.Placeholder"/>).
/// </summary>
[HarmonyPatch(typeof(FurnitureGenerator), nameof(FurnitureGenerator.SpawnFurniture),
    new[] { typeof(int), typeof(Vector3), typeof(Quaternion), typeof(Transform) })]
internal static class SpawnFromSavePatch
{
    private static bool Prefix(int id, Vector3 position, Quaternion rotation, Transform parent, ref GameObject __result)
    {
        if (id != ComputerFurniture.FurnitureId) return true;
        try { __result = ComputerSpawner.Spawn(position, rotation, parent, -1); }
        catch (Exception e) { Plugin.Error("Spawning a saved computer failed: " + e); __result = null; }
        if (__result == null)
        {
            try { __result = ComputerSpawner.Placeholder(position, rotation, parent); }
            catch (Exception e) { Plugin.Error("Creating a stand-in for a saved computer failed: " + e); }
        }
        return false;
    }
}

/// <summary>Other players' copies: make sure the prefab exists before Photon looks it up.</summary>
[HarmonyPatch(typeof(DefaultPool), nameof(DefaultPool.Instantiate))]
internal static class PhotonPoolPatch
{
    private static void Prefix(string prefabId)
    {
        if (prefabId != ComputerFurniture.PrefabId) return;
        try
        {
            ComputerFurniture.EnsureSo();
            if (ComputerFurniture.EnsureTemplate()) ComputerFurniture.RegisterWithPhoton();
            else Plugin.Error("Another player placed a computer, but its prefab couldn't be built here");
        }
        catch (Exception e) { Plugin.Error("Preparing a networked computer failed: " + e.Message); }
    }
}

// ------------------------------------------------------------------ using it

[HarmonyPatch(typeof(Furniture), nameof(Furniture.InstantInteract))]
internal static class UseComputerPatch
{
    private static bool Prefix(Furniture __instance, ref bool __result)
    {
        if (!ComputerFurniture.IsOurs(__instance)) return true;
        try
        {
            var pm = __instance.m_PlacingMode;
            if (pm != null && pm.IsPlacingMode) return true;
            __result = ComputerSession.TryStart(__instance);
            return false;
        }
        catch (Exception e)
        {
            Plugin.Error("Using the computer failed: " + e);
            ComputerSession.End("error");
            return true;
        }
    }
}

/// <summary>Plain furniture is hold-only; the computer can be used (click) and moved (hold).</summary>
[HarmonyPatch(typeof(Furniture), nameof(Furniture.InteractionType), MethodType.Getter)]
internal static class InteractionTypePatch
{
    private static void Postfix(Furniture __instance, ref InteractionType __result)
    {
        if (ComputerFurniture.IsOurs(__instance)) __result = InteractionType.BOTH;
    }
}

[HarmonyPatch(typeof(Furniture), nameof(Furniture.InstantInteractionHint), MethodType.Getter)]
internal static class InteractionHintPatch
{
    private static void Postfix(Furniture __instance, ref string __result)
    {
        if (ComputerFurniture.IsOurs(__instance)) __result = "_interact"; // same hint as the store computer
    }
}

[HarmonyPatch(typeof(Computer), nameof(Computer.Back))]
internal static class ComputerBackPatch
{
    private static void Prefix() => ComputerSession.InBack = true;

    /// <summary>Runs even if vanilla Back throws, so InBack can't stay stuck (it would block ComputerClosePatch).</summary>
    private static void Finalizer() => ComputerSession.InBack = false;

    private static void Postfix(Computer __instance)
    {
        ComputerSession.InBack = false;
        // Back has put the player at m_PlayerPosition (still on the extra computer). Save this computer's screen and
        // end the session; the borrowed camera stays on the extra computer until the exit blend is done.
        try { ComputerSession.AfterClose(__instance, "left the computer"); }
        catch (Exception e) { Plugin.Error("Ending the computer session failed: " + e); }
    }
}

/// <summary>
/// The store computer is just another computer: its apps and tabs are restored right before it opens, so what an extra
/// computer left open doesn't show up there. Extra computers call this method themselves with a session active, which
/// is skipped here (ComputerSession.TryStart applies their own state).
/// </summary>
[HarmonyPatch(typeof(Computer), nameof(Computer.InstantInteract))]
internal static class StoreComputerScreenPatch
{
    private static void Prefix(Computer __instance)
    {
        try
        {
            if (ComputerSession.Active) return;
            ComputerUiState.DropPending();
            if (ComputerSession.Lingering) ComputerSession.FinishLinger("store computer used"); // it needs its own camera back
            if (!Plugin.IndependentScreens.Value) return;
            if (__instance.IsOccupied || __instance.OpenPC || __instance.IsInPlacingMode) return;
            ComputerUiState.Apply(__instance, ComputerUiState.StoreKey);
        }
        catch (Exception e) { Plugin.Error("Setting up the store computer's screen failed: " + e.Message); }
    }

    private static void Postfix(Computer __instance, bool __result)
    {
        if (ComputerSession.Active) return;
        if (__result)
        {
            ComputerSession.StoreSessionOpen = true;
            if (Plugin.IndependentScreens.Value)
            {
                try { ComputerUiState.Finish(__instance); }
                catch (Exception e) { Plugin.Error("Opening the store computer's windows failed: " + e.Message); }
            }
        }
    }
}

/// <summary>Any other way the computer gets closed.</summary>
[HarmonyPatch(typeof(Computer), nameof(Computer.OpenPC), MethodType.Setter)]
internal static class ComputerClosePatch
{
    private static void Postfix(Computer __instance, bool value)
    {
        if (value || !ComputerSession.Active || ComputerSession.InBack) return;
        try { ComputerSession.AfterClose(__instance, "computer closed"); }
        catch (Exception e) { Plugin.Error("Ending the computer session failed: " + e); }
    }
}

[HarmonyPatch(typeof(Computer), nameof(Computer.LateUpdate))]
internal static class ComputerLateUpdatePatch
{
    private static void Postfix()
    {
        try
        {
            if (ComputerSession.Active || ComputerSession.Lingering) ComputerSession.SyncAnchors();
            ComputerSession.TickLinger();
        }
        catch (Exception e) { LogOnce("Moving the borrowed computer screen failed: " + e.Message); }
        try { Presence.Tick(); }
        catch (Exception e) { LogOnce("Multiplayer check failed: " + e.Message); }
    }

    private static string _lastError;

    /// <summary>Runs every frame: log a repeated error only once.</summary>
    private static void LogOnce(string msg)
    {
        if (msg == _lastError) return;
        _lastError = msg;
        Plugin.Error(msg);
    }
}

/// <summary>Using an extra computer must not mark the store computer as occupied for the others.</summary>
[HarmonyPatch(typeof(NetworkComputer), nameof(NetworkComputer.InteractComputer_Broadcast))]
internal static class OccupiedBroadcastPatch
{
    private static bool Prefix(bool willOpen)
    {
        if (!ComputerSession.Active) return true;
        Plugin.Debug($"Not telling other players the store computer is {(willOpen ? "in use" : "free")} (extra computer)");
        return false;
    }
}
