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

internal static class OpenBoxGate
{
    /// <summary>True when this is a computer box and a player without the mod is in the room.</summary>
    internal static bool Refuse(FurnitureBoxInteraction interaction, string side)
    {
        try
        {
            var data = interaction?.CurrentFurnitureBox?.Data;
            if (data == null || data.FurnitureID != ComputerFurniture.FurnitureId) return false;
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
            return LeanPool.Spawn(ComputerFurniture.Template, pos, rot, parent);
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
        return PhotonNetwork.InstantiateRoomObject(ComputerFurniture.PrefabId, pos, rot, 0, data);
    }
}

/// <summary>Unboxing: the game spawns the furniture facing the player.</summary>
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
                if (fwd.sqrMagnitude > 1e-6f) rot = Quaternion.LookRotation(-fwd.normalized, Vector3.up);
            }
            __result = ComputerSpawner.Spawn(position, rot, __instance.transform, furnitureBoxInteractionViewId);
        }
        catch (Exception e) { Plugin.Error("Spawning a computer failed: " + e); __result = null; }
        return false;
    }
}

/// <summary>Loading a save.</summary>
[HarmonyPatch(typeof(FurnitureGenerator), nameof(FurnitureGenerator.SpawnFurniture),
    new[] { typeof(int), typeof(Vector3), typeof(Quaternion), typeof(Transform) })]
internal static class SpawnFromSavePatch
{
    private static bool Prefix(int id, Vector3 position, Quaternion rotation, Transform parent, ref GameObject __result)
    {
        if (id != ComputerFurniture.FurnitureId) return true;
        try { __result = ComputerSpawner.Spawn(position, rotation, parent, -1); }
        catch (Exception e) { Plugin.Error("Spawning a saved computer failed: " + e); __result = null; }
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

    private static void Postfix()
    {
        ComputerSession.InBack = false;
        // Back has put the player at m_PlayerPosition (still on the extra computer); now restore.
        if (ComputerSession.Active) ComputerSession.End("left the computer");
    }
}

/// <summary>Any other way the computer gets closed.</summary>
[HarmonyPatch(typeof(Computer), nameof(Computer.OpenPC), MethodType.Setter)]
internal static class ComputerClosePatch
{
    private static void Postfix(bool value)
    {
        if (!value && ComputerSession.Active && !ComputerSession.InBack)
            ComputerSession.End("computer closed");
    }
}

[HarmonyPatch(typeof(Computer), nameof(Computer.LateUpdate))]
internal static class ComputerLateUpdatePatch
{
    private static void Postfix()
    {
        if (ComputerSession.Active) ComputerSession.SyncAnchors();
        Presence.Tick();
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
