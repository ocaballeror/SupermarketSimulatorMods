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
