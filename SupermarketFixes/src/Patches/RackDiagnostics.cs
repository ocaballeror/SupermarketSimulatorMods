using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using DG.Tweening;
using Il2CppInterop.Runtime;
using Photon.Pun;
using Object = UnityEngine.Object;

namespace SupermarketFixes.Patches;

/// <summary>Consistency checks for racks and their boxes. Logging only; changes nothing.</summary>
internal static class RackDiagnostics
{
    private static bool On => Plugin.Active && Plugin.VerboseLogging.Value;

    internal static string Describe(Box box)
    {
        var t = box.transform;
        var p = t.position;
        var data = box.Data;
        var col = box.m_MainCollider;
        var rend = box.m_StaticRenderer;
        var net = box.NetworkBox;
        var view = net != null ? net.PhotonView : null;
        return $"uid={(data != null ? data.UID : -1)} product={(data != null ? data.ProductID : -1)} " +
               $"networked={net != null}{(view != null ? $" view={view.ViewID}" : "")} pos=({p.x:F2},{p.y:F2},{p.z:F2}) " +
               $"parent={(t.parent != null ? t.parent.name : "<none>")} active={box.gameObject.activeInHierarchy} " +
               $"racked={box.m_Racked} instanced={RackMoveFix.IsRegistered(box)} collider={(col != null && col.enabled)} " +
               $"meshVisible={(rend != null && !rend.forceRenderingOff)} tweening={DOTween.IsTweening(t)}";
    }

    internal static void AfterPlace(Rack rack)
    {
        if (!On) return;
        Audit(rack, "after place");
        // Let reposition tweens / delayed callbacks settle, then look for leftovers store-wide.
        DOVirtual.DelayedCall(2f, (TweenCallback)(Action)(() => { if (On) ScanStore("2s after place"); }));
    }

    internal static void Audit(Rack rack, string phase)
    {
        if (!On || rack == null) return;
        try
        {
            var sb = new StringBuilder();
            int slotIdx = -1;
            foreach (var slot in RackMoveFix.Slots(rack))
            {
                slotIdx++;
                var boxes = slot.m_Boxes;
                int boxCount = boxes?.Count ?? 0, dataCount = slot.m_Data?.RackedBoxDatas?.Count ?? 0;
                if (boxCount != dataCount)
                    sb.AppendLine($"  slot {slotIdx}: m_Boxes={boxCount} but RackedBoxDatas={dataCount}");
                for (int i = 0; i < boxCount; i++)
                {
                    var box = boxes[i];
                    if (box == null) { sb.AppendLine($"  slot {slotIdx}: m_Boxes[{i}] is destroyed/null"); continue; }
                    bool badParent = box.transform.parent == null || box.transform.parent.Pointer != slot.transform.Pointer;
                    bool inactive = !box.gameObject.activeInHierarchy;
                    if (badParent || inactive)
                        sb.AppendLine($"  slot {slotIdx}: {(badParent ? "NOT-PARENTED-TO-SLOT " : "")}{(inactive ? "INACTIVE " : "")}{Describe(box)}");
                }
            }
            if (sb.Length > 0)
                Plugin.Logger.LogWarning($"[RackMove] Rack {rack.ID} {phase}: inconsistencies\n{sb}");
        }
        catch (Exception e) { Plugin.Logger.LogError($"[RackMove] Audit failed: {e}"); }
    }

    /// <summary>Find boxes that are racked / drawn as racked but belong to no rack slot, and box records
    /// that exist as more than one live box.</summary>
    internal static void ScanStore(string reason)
    {
        try
        {
            var owned = new HashSet<IntPtr>();
            var sb = new StringBuilder();
            int stale = 0;
            var racks = Object.FindObjectsOfType(Il2CppType.Of<Rack>());
            foreach (var o in racks)
            {
                var rack = o.TryCast<Rack>();
                int slotIdx = -1;
                foreach (var slot in RackMoveFix.Slots(rack))
                {
                    slotIdx++;
                    var boxes = slot.m_Boxes;
                    for (int i = 0; boxes != null && i < boxes.Count; i++)
                    {
                        var box = boxes[i];
                        if (box == null) continue;
                        // Listed by this slot but not on it: held, loose, or on another rack (a stale entry).
                        if (!owned.Add(box.Pointer) || box.transform.parent == null ||
                            box.transform.parent.Pointer != slot.transform.Pointer)
                        {
                            stale++;
                            sb.AppendLine($"  STALE rack {rack.ID} slot {slotIdx} lists a box that isn't on it: {Describe(box)}");
                        }
                    }
                    // A box record with no box in the slot: a ghost copy for joining guests / a duplicate on reload.
                    var datas = slot.m_Data?.RackedBoxDatas;
                    for (int i = 0; datas != null && i < datas.Count; i++)
                    {
                        var rec = datas[i];
                        if (rec == null || HasBoxFor(boxes, rec)) continue;
                        stale++;
                        sb.AppendLine($"  STALE rack {rack.ID} slot {slotIdx} has a record with no box: uid={rec.UID} product={rec.ProductID}");
                    }
                }
            }

            int orphans = 0, total = 0;
            var byUid = new Dictionary<int, List<Box>>();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Box>()))
            {
                var box = o.TryCast<Box>();
                if (box == null) continue;
                total++;
                var uid = box.Data?.UID;
                if (uid > 0 && box.gameObject.activeInHierarchy) // real UIDs are positive; 0 = detached/unassigned
                {
                    if (!byUid.TryGetValue(uid.Value, out var same)) byUid[uid.Value] = same = new List<Box>();
                    same.Add(box);
                }
                if (owned.Contains(box.Pointer) || (!box.m_Racked && !RackMoveFix.IsRegistered(box))) continue;
                orphans++;
                sb.AppendLine("  ORPHAN " + Describe(box));
            }
            // Several live boxes for one box record = a duplicate (e.g. networked box + local rack copy).
            int dupGroups = 0;
            foreach (var kv in byUid)
            {
                if (kv.Value.Count < 2) continue;
                dupGroups++;
                sb.AppendLine($"  DUPLICATE uid={kv.Key} x{kv.Value.Count}:");
                foreach (var b in kv.Value)
                    sb.AppendLine($"    {(owned.Contains(b.Pointer) ? "in-slot " : "no-slot ")}{Describe(b)}");
            }

            string role = !PhotonNetwork.InRoom ? "offline" : PhotonNetwork.IsMasterClient ? "host" : "guest";
            if (orphans > 0 || dupGroups > 0 || stale > 0)
                Plugin.Logger.LogWarning($"[RackMove] Store scan ({reason}, {role}): {orphans} orphan box(es), {dupGroups} duplicated record(s), " +
                                         $"{stale} stale rack entr(ies) of {total} boxes, {owned.Count} boxes on {racks.Length} racks\n{sb}");
            else
                Plugin.Logger.LogInfo($"[RackMove] Store scan ({reason}, {role}): no orphan, duplicate or stale boxes " +
                                      $"({total} boxes, {owned.Count} on {racks.Length} racks)");
        }
        catch (Exception e) { Plugin.Logger.LogError($"[RackMove] Store scan failed: {e}"); }
    }

    private static bool HasBoxFor(Il2CppSystem.Collections.Generic.List<Box> boxes, BoxData rec)
    {
        for (int i = 0; boxes != null && i < boxes.Count; i++)
        {
            var b = boxes[i];
            var d = b?.Data;
            if (d != null && (d.Pointer == rec.Pointer || (d.UID == rec.UID && d.ProductID == rec.ProductID))) return true;
        }
        return false;
    }
}
