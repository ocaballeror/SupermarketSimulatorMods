/*
 * ExtraComputers: the "Computer" furniture type (furniture definition, prefab, icon).
 *
 * How the game handles furniture (verified in the native dumps):
 *  - Every furniture type is a FurnitureSO {ID, BoxSize, FurnitureIcon, AtlasIndex/AtlasPosition,
 *    FurniturePrefab, FurnitureColor, IsMainFurniture, Cost} in IDManager.m_Furnitures.
 *    IDManager.FurnitureSO(id) is a FirstOrDefault over that list and is what the cart, the
 *    delivery, the furniture box, box-up and the spawner all use.
 *  - The shop (FurnituresViewer.SpawnFurnitures) lists every SO in m_Furnitures with
 *    IsMainFurniture, except id 7000. The item name comes from
 *    LocalizationManager.LocalizedFurnitureName(id), the shop icon from SO.FurnitureIcon.
 *  - The icon printed on the furniture box is ProductAtlasManager.SetFurnitureIcon(id, renderer):
 *    sharedMaterial = atlasedMaterials[SO.AtlasIndex], then a "_BaseMap" UV offset from AtlasPosition.
 *  - Placed furniture is spawned by FurnitureGenerator.SpawnFurniture(...): offline through
 *    LeanPool.Spawn(prefab), online (master only) through PhotonNetwork.InstantiateRoomObject(
 *    "Furnitures/<Type>/<prefab name>", pos, rot, 0, {furnitureBoxInteractionViewId}) and Photon's
 *    DefaultPool (ResourceCache, then Resources.Load). NetworkFurniture.OnPhotonInstantiate reads that
 *    view id, gives ownership to the player who opened the box and starts their placing mode.
 *  - Furniture.m_ID is serialized in the prefab (nothing sets it at runtime) and is what gets saved.
 *  - Moving and placing is driven by FurniturePlacingMode (colliders, renderers for the hologram,
 *    placing tags, navmesh objects). Its Awake recomputes the runtime lists from m_Furniture.
 *  - PlayerInteraction.Raycast takes the IInteractable from the GameObject of the collider it hits,
 *    so the furniture root itself needs a collider.
 *
 * What this mod builds (once per game session, on the first game scene):
 *  - A FurnitureSO copied from an existing one, with our id, price, icon, the most common furniture
 *    box size and IsMainFurniture = true. It is added to IDManager.m_Furnitures, and
 *    IDManager.FurnitureSO(our id) is answered directly so lookups work even before that.
 *  - A prefab template, kept under an inactive DontDestroyOnLoad holder (so nothing in it ever wakes): a copy
 *    of the store computer's root. That root already has what a movable networked object needs (layer 6
 *    colliders that the interaction raycast hits, PhotonView + PhotonTransformView, NavMeshObstacle,
 *    Highlightable, snap points, and a "Placing Mode" child with its FurniturePlacingMode, whose references
 *    Instantiate remaps into the copy). The PC camera, the screen UI canvases and every computer script
 *    (Computer, ComputerOperatingSystem, MusicPlayerManager, NetworkComputer, ...) are removed; the placing
 *    mode's interaction-zone canvas is kept. Furniture + NetworkFurniture are added (the game has no
 *    Furniture subclasses, every furniture uses the plain class plus type scripts). The PhotonView loses its
 *    scene view id and takes the ownership option of a real furniture prefab.
 *    Before v1.2.0 the template was built on a "donor" furniture prefab; no furniture qualified in-game
 *    ("no usable furniture prefab", 2026-11-15), so nothing could be spawned and opening a computer box left
 *    the player in an empty placing mode.
 *    The template is registered in Photon's DefaultPool.ResourceCache under PrefabId.
 *  - An icon rendered from the model (fallback: a drawn monitor icon). The game ships no computer icon.
 *  - If the template can't be built, computer boxes refuse to open (OpenBoxGate), so the player keeps the box.
 *
 * The furniture can then be bought, delivered in a box, unboxed, placed, moved, boxed up and saved
 * through the game's own code. Using it is handled by ComputerSession.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using __Project__.Scripts.Computer;
using __Project__.Scripts.Multiplayer;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace ExtraComputers;

internal static class ComputerFurniture
{
    public const int FurnitureId = 917001;
    public const string PrefabId = "Furnitures/ExtraComputers/ExtraComputer";
    public const string ScreenMarker = "#ecscreen";
    public const string DisplayName = "Computer";

    internal static FurnitureSO So;
    internal static GameObject Template;
    /// <summary>The computer's entry in the furniture shop (built once per scene by FurnituresViewer.Start).</summary>
    internal static FurnitureSalesItem ShopItem;
    internal static Texture2D IconTexture;
    private static Sprite _iconSprite;
    private static bool _iconRendered;
    private static int _iconRenderAttempts;
    private static Material _boxIconMaterial;
    private static GameObject _holder;
    private static int _buildAttempts;
    private static Computer _computer;

    // Relative layout of the real computer, measured when the template is built.
    internal static Vector3 ScreenLocalPos, CamLocalPos;
    internal static bool HaveFrontDirection;

    // ---------------------------------------------------------------- lookups

    internal static Computer FindRealComputer()
    {
        if (_computer != null) return _computer;
        try
        {
            var all = Object.FindObjectsByType<Computer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null || !c.gameObject.scene.IsValid()) continue;
                _computer = c;
                break;
            }
        }
        catch (Exception e) { Plugin.Warn("Looking for the store computer failed: " + e.Message); }
        return _computer;
    }

    /// <summary>The transform the game moves when the store computer is moved.</summary>
    internal static Transform ComputerRoot(Computer comp)
    {
        var pm = comp.m_PlacingMode;
        return pm != null && pm.m_Furniture != null ? pm.m_Furniture.transform : comp.transform;
    }

    /// <summary>Where the front of the computer is (the side the PC camera looks from), for the icon.</summary>
    private static void MeasureFront(Computer comp, Transform compRoot)
    {
        if (comp.m_PCCam == null || comp.m_ComputerScreen == null) return;
        CamLocalPos = compRoot.InverseTransformPoint(comp.m_PCCam.transform.position);
        ScreenLocalPos = compRoot.InverseTransformPoint(comp.m_ComputerScreen.transform.position);
        HaveFrontDirection = true;
    }

    internal static IDManager Ids => NoktaSingleton<IDManager>.HasInstance ? NoktaSingleton<IDManager>.Instance : null;

    internal static bool IsOurs(Furniture f) => f != null && f.m_ID == FurnitureId;

    // ---------------------------------------------------------------- furniture definition

    /// <summary>Creates our FurnitureSO if needed and makes sure the current IDManager lists it.</summary>
    internal static FurnitureSO EnsureSo(IDManager ids = null)
    {
        ids ??= Ids;
        if (ids == null) return So;
        var list = ids.m_Furnitures;
        if (list == null) return So;

        if (So == null)
        {
            FurnitureSO donor = null;
            var sizes = new Dictionary<BoxSize, int>();
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s == null || s.ID == FurnitureId || !s.IsMainFurniture) continue;
                donor ??= s;
                sizes[s.BoxSize] = sizes.TryGetValue(s.BoxSize, out var n) ? n + 1 : 1;
            }
            if (donor == null)
            {
                Plugin.Warn("No furniture definitions found yet; can't add the computer to the shop.");
                return null;
            }

            So = Object.Instantiate(donor);
            So.name = "ExtraComputerSO";
            So.hideFlags = HideFlags.DontUnloadUnusedAsset;
            So.ID = FurnitureId;
            So.IsMainFurniture = true;
            So.BoxSize = sizes.OrderByDescending(kv => kv.Value).First().Key;
            So.AtlasIndex = 0;
            So.FurnitureColor = new Color(0.25f, 0.45f, 0.85f, 1f);
            So.FurniturePrefab = Template;
            ApplyPrice();
            Plugin.Info($"Computer furniture registered: id {FurnitureId}, box {So.BoxSize}, price {So.Cost} " +
                        $"(definition copied from id {donor.ID})");
            if (Plugin.Verbose.Value) LogFurnitureList(list);
        }

        bool listed = false;
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null && list[i].Pointer == So.Pointer) { listed = true; break; }
        if (!listed) list.Add(So);

        if (So.FurniturePrefab == null && Template != null) So.FurniturePrefab = Template;
        return So;
    }

    /// <summary>Sets the shop price: the config value, or the host's published value when this client is a guest.</summary>
    internal static void ApplyPrice()
    {
        if (So == null) return;
        float price = Presence.EffectivePrice();
        if (So.Cost != price)
        {
            So.Cost = price;
            Plugin.Debug($"Computer price is now {price}");
        }
    }

    /// <summary>The mod was switched on or off: the shop item follows (the furniture definition itself stays
    /// listed, so boxes and computers that already exist keep resolving their definition).</summary>
    internal static void OnActiveChanged(bool on)
    {
        if (So != null) So.IsMainFurniture = on;
        // Refresh the cached state before the shop item is shown again, but only once the item exists (so never
        // from BasePlugin.Load, where Photon statics must not be touched); Tick and ShopItemPatch refresh it anyway.
        if (on && ShopItem != null) Presence.Blocked();
        RefreshShopItem();
    }

    /// <summary>Shows the shop entry only while the mod is on and every other player has it.</summary>
    internal static void RefreshShopItem()
    {
        var item = ShopItem;
        if (item == null) return;
        bool show = Plugin.Active && !Presence.CachedBlocked;
        var go = item.gameObject;
        if (go != null && go.activeSelf != show)
        {
            go.SetActive(show);
            Plugin.Debug(show ? "Computer shown in the shop" : "Computer hidden in the shop");
        }
    }

    private static void LogFurnitureList(Il2CppSystem.Collections.Generic.List<FurnitureSO> list)
    {
        var sb = new StringBuilder("Existing furniture definitions (id: prefab, box, cost, main):");
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s == null) continue;
            var p = s.FurniturePrefab;
            sb.Append($"\n  {s.ID}: {(p == null ? "<no prefab>" : p.name)}, {s.BoxSize}, {s.Cost}, {s.IsMainFurniture}");
        }
        Plugin.Debug(sb.ToString());
    }

    // ---------------------------------------------------------------- prefab template

    internal static bool EnsureTemplate(IDManager ids = null)
    {
        if (Template != null) return true;
        if (_buildAttempts >= 3) return false;
        var comp = FindRealComputer();
        ids ??= Ids;
        if (comp == null || ids == null) return false;

        _buildAttempts++;
        try
        {
            BuildTemplate(comp, ids);
        }
        catch (Exception e)
        {
            Plugin.Error("Building the computer furniture failed: " + e);
            if (_holder != null) Object.DestroyImmediate(_holder);
            _holder = null;
            Template = null;
        }
        if (Template != null)
        {
            RegisterWithPhoton();
            if (So != null) So.FurniturePrefab = Template;
        }
        return Template != null;
    }

    internal static void RegisterWithPhoton()
    {
        if (Template == null) return;
        try
        {
            var pool = PhotonNetwork.PrefabPool;
            var dp = pool?.TryCast<DefaultPool>();
            if (dp == null)
            {
                Plugin.Error("Photon's prefab pool isn't the default pool; extra computers won't appear for other players.");
                return;
            }
            dp.ResourceCache[PrefabId] = Template;
        }
        catch (Exception e) { Plugin.Error("Registering the computer with Photon failed: " + e.Message); }
    }

    private static void BuildTemplate(Computer comp, IDManager ids)
    {
        // The transform the game moves when the computer itself is moved is the computer's root.
        var compPm = comp.m_PlacingMode;
        Transform compRoot = ComputerRoot(comp);

        if (Plugin.Verbose.Value)
            Plugin.Debug("Store computer hierarchy:\n" + Dump(compRoot) +
                         $"\n  PCCam={Path(compRoot, comp.m_PCCam?.transform)} Screen={Path(compRoot, comp.m_ComputerScreen?.transform)} " +
                         $"PlayerPosition={Path(compRoot, comp.m_PlayerPosition)} PlacingMode={Path(compRoot, compPm?.transform)} " +
                         $"ZoneCanvas={Path(compRoot, compPm?.m_InteractionZoneCanvas?.transform)}");
        if (compPm == null) throw new Exception("the store computer has no FurniturePlacingMode");

        string camPath = comp.m_PCCam != null ? Path(compRoot, comp.m_PCCam.transform) : null;
        string screenPath = comp.m_ComputerScreen != null ? Path(compRoot, comp.m_ComputerScreen.transform) : null;
        string zonePath = compPm.m_InteractionZoneCanvas != null ? Path(compRoot, compPm.m_InteractionZoneCanvas.transform) : null;
        MeasureFront(comp, compRoot);

        _holder = new GameObject("ExtraComputers.TemplateHolder");
        _holder.SetActive(false);
        Object.DontDestroyOnLoad(_holder);

        // A copy of the store computer. It is created under the inactive holder, so none of its scripts wake up.
        // Its own setup is already what a movable networked object needs: the root (layer, colliders, PhotonView,
        // transform view, navmesh obstacle, highlight, snap points) and a FurniturePlacingMode whose references
        // (m_Furniture, renderers, colliders, parent PhotonView) Instantiate has remapped into this copy.
        var tpl = Object.Instantiate(compRoot.gameObject, _holder.transform);
        tpl.name = "ExtraComputer";
        tpl.transform.localPosition = Vector3.zero;
        tpl.transform.localRotation = Quaternion.identity;
        tpl.transform.localScale = compRoot.lossyScale;

        var zone = zonePath != null ? Find(tpl.transform, zonePath) : null;
        StripComputerCopy(tpl.transform, camPath, screenPath, zone);

        var tplPm = Find(tpl.transform, Path(compRoot, compPm.transform))?.GetComponent<FurniturePlacingMode>();
        if (tplPm == null) throw new Exception("the placing settings weren't found in the computer copy");
        tplPm.m_Furniture = tpl;

        var view = tpl.GetComponent<PhotonView>();
        if (view == null) throw new Exception("the store computer has no PhotonView on its root");
        SetUpPhotonView(view, tpl.transform, ids);
        tplPm.m_ParentPhotonView = view;

        // The furniture scripts, which the store computer doesn't have.
        var furniture = tpl.AddComponent<Furniture>();
        var net = tpl.AddComponent<NetworkFurniture>();
        furniture.m_ID = FurnitureId;
        furniture.m_PlacingMode = tplPm;
        furniture.m_PhotonView = view;
        furniture.m_NetworkFurniture = net;
        net.m_PhotonView = view;
        net.m_Furniture = furniture;

        Template = tpl;
        Plugin.Info("Computer furniture prefab built from a copy of the store computer");
        Plugin.Debug($"Placing settings: tags [{string.Join(",", Strings(tplPm.m_PlacingTag))}], " +
                     $"renderers {tplPm.m_Renderers?.Length ?? 0}, owner colliders {tplPm.m_OwnerCollider?.Count ?? 0}, " +
                     $"placing box {(tplPm.m_PlacingModeCollider == null ? "none" : tplPm.m_PlacingModeCollider.name)}, " +
                     $"zone canvas {(tplPm.m_InteractionZoneCanvas == null ? "none" : tplPm.m_InteractionZoneCanvas.name)}");
        if (Plugin.Verbose.Value) Plugin.Debug("Computer furniture prefab:\n" + Dump(tpl.transform));
    }

    /// <summary>The copy's PhotonView: only components that are still there are observed, no scene view id (the clone
    /// would otherwise claim the store computer's id offline), and the ownership settings of a real furniture prefab
    /// (NetworkFurniture.OnPhotonInstantiate transfers ownership to the player who opened the box).</summary>
    private static void SetUpPhotonView(PhotonView view, Transform tpl, IDManager ids)
    {
        view.sceneViewId = 0;
        view.RpcMonoBehaviours = null;
        var observed = new Il2CppSystem.Collections.Generic.List<Component>();
        var src = view.ObservedComponents;
        if (src != null)
            for (int i = 0; i < src.Count; i++)
                if (src[i] != null && src[i].transform.IsChildOf(tpl)) observed.Add(src[i]);
        view.ObservedComponents = observed;

        PhotonView reference = null;
        string refName = null;
        var list = ids.m_Furnitures;
        for (int i = 0; i < list.Count && reference == null; i++)
        {
            var s = list[i];
            if (s == null || s.ID == FurnitureId || s.FurniturePrefab == null) continue;
            if (s.FurniturePrefab.GetComponent<Furniture>() == null) continue;
            reference = s.FurniturePrefab.GetComponent<PhotonView>();
            refName = s.FurniturePrefab.name;
        }
        var before = view.OwnershipTransfer;
        if (reference != null)
        {
            view.OwnershipTransfer = reference.OwnershipTransfer;
            view.Synchronization = reference.Synchronization;
        }
        else if (view.OwnershipTransfer == OwnershipOption.Fixed)
        {
            view.OwnershipTransfer = OwnershipOption.Takeover;
        }
        var names = new List<string>();
        for (int i = 0; i < observed.Count; i++) names.Add(observed[i].GetIl2CppType().Name);
        Plugin.Debug($"PhotonView: ownership {before} -> {view.OwnershipTransfer} (from {refName ?? "no furniture prefab"}), " +
                     $"sync {view.Synchronization}, observes [{string.Join(",", names)}]");
    }

    private static readonly HashSet<string> KeptModelComponents = new()
    {
        "Transform", "RectTransform", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "LODGroup",
        "BoxCollider", "MeshCollider", "SphereCollider", "CapsuleCollider", "Rigidbody",
        "NavMeshObstacle", "NavmeshBuildSourceObject", "FurniturePlacingMode", "Highlightable",
        "FurnitureSnapPointSetup", "FurnitureSnapPoint",
        "PhotonView", "PhotonTransformView", "PhotonTransformViewOptimized",
    };

    /// <summary>Removes the PC camera, the screen UI and every computer script from the copy. The placing mode's
    /// interaction-zone canvas is kept as it is (the placing mode shows it while moving).</summary>
    private static void StripComputerCopy(Transform model, string camPath, string screenPath, Transform keep)
    {
        if (camPath != null)
        {
            var cam = Find(model, camPath);
            if (cam != null && cam != model) Object.DestroyImmediate(cam.gameObject);
        }
        DestroyCanvasSubtrees(model, keep);

        // Mark what's left of the screen object so a session can hide it while the real screen sits there.
        if (screenPath != null)
        {
            var screen = Find(model, screenPath);
            if (screen != null)
            {
                var rends = screen.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < rends.Length; i++) rends[i].gameObject.name += ScreenMarker;
            }
        }

        var removed = new List<string>();
        for (int pass = 0; pass < 3; pass++)
        {
            var comps = model.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                if (keep != null && c.transform.IsChildOf(keep)) continue;
                var name = c.GetIl2CppType().Name;
                if (KeptModelComponents.Contains(name)) continue;
                if (pass == 0 && c.TryCast<Behaviour>() == null) continue; // scripts first, then the rest
                removed.Add(name);
                Object.DestroyImmediate(c);
            }
        }
        if (removed.Count > 0)
            Plugin.Debug("Removed from the computer copy: " + string.Join(", ", removed.Distinct()));
    }

    private static void DestroyCanvasSubtrees(Transform root, Transform keep)
    {
        var canvases = root.GetComponentsInChildren<Canvas>(true);
        var gos = new List<GameObject>();
        for (int i = 0; i < canvases.Length; i++)
        {
            var c = canvases[i];
            if (c == null || c.transform == root) continue;
            if (keep != null && c.transform.IsChildOf(keep)) continue;
            gos.Add(c.gameObject);
        }
        foreach (var go in gos)
            if (go != null) Object.DestroyImmediate(go);
    }

    private static IEnumerable<string> Strings(Il2CppSystem.Collections.Generic.List<string> l)
    {
        if (l == null) yield break;
        for (int i = 0; i < l.Count; i++) yield return l[i];
    }

    // ---------------------------------------------------------------- icon

    /// <summary>The game has no computer furniture icon (the furniture icon atlas has none, and the only computer-named
    /// sprite, T_Vending_ComputerIcon, is a vending machine silhouette), so the icon is rendered from the computer's
    /// model, like the other furniture icons. The drawn fallback is retried a few times, because the store computer
    /// may not exist yet when the shop first asks for the icon.</summary>
    internal static void EnsureIcon()
    {
        if (So == null) return;
        if (!_iconRendered && _iconRenderAttempts < 3)
        {
            Transform model = EnsureTemplate() ? Template.transform : null;
            if (model == null)
            {
                var comp = FindRealComputer();
                if (comp != null) { model = ComputerRoot(comp); MeasureFront(comp, model); }
            }
            Texture2D tex = null;
            if (model != null)
            {
                _iconRenderAttempts++;
                tex = IconMaker.Render(model);
            }
            if (tex != null) _iconRendered = true;
            else if (IconTexture == null) tex = IconMaker.Draw();
            if (tex != null) SetIconTexture(tex);
        }
        if (IconTexture == null) return;
        if (_iconSprite == null)
        {
            _iconSprite = Sprite.Create(IconTexture, new Rect(0, 0, IconTexture.width, IconTexture.height), new Vector2(0.5f, 0.5f), 100f);
            _iconSprite.name = "ExtraComputerIcon";
            _iconSprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        }
        if (So.FurnitureIcon == null || So.FurnitureIcon.Pointer != _iconSprite.Pointer) So.FurnitureIcon = _iconSprite;
    }

    private static void SetIconTexture(Texture2D tex)
    {
        tex.name = "ExtraComputerIcon";
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        IconTexture = tex;
        _iconSprite = null; // rebuilt from the new texture
        if (_boxIconMaterial != null)
            foreach (var prop in new[] { "_BaseMap", "_MainTex" })
                if (_boxIconMaterial.HasProperty(prop)) _boxIconMaterial.SetTexture(prop, tex);
        Plugin.Debug(_iconRendered ? "Computer icon rendered from the model" : "Computer icon: using the drawn placeholder for now");
    }

    /// <summary>The furniture box's label: a copy of the game's icon-atlas material showing our icon.</summary>
    internal static bool ApplyBoxIcon(MeshRenderer rend)
    {
        if (rend == null) return false;
        EnsureIcon();
        if (IconTexture == null) return false;
        if (_boxIconMaterial == null)
        {
            var atlas = NoktaSingleton<ProductAtlasManager>.HasInstance ? NoktaSingleton<ProductAtlasManager>.Instance : null;
            var mats = atlas?.atlasedMaterials;
            if (mats == null || mats.Count == 0 || mats[0] == null) return false;
            _boxIconMaterial = new Material(mats[0]) { name = "ExtraComputerBoxIcon" };
            _boxIconMaterial.hideFlags = HideFlags.DontUnloadUnusedAsset;
            foreach (var prop in new[] { "_BaseMap", "_MainTex" })
            {
                if (!_boxIconMaterial.HasProperty(prop)) continue;
                _boxIconMaterial.SetTexture(prop, IconTexture);
                _boxIconMaterial.SetTextureScale(prop, Vector2.one);
                _boxIconMaterial.SetTextureOffset(prop, Vector2.zero);
            }
        }
        rend.sharedMaterial = _boxIconMaterial;
        return true;
    }

    // ---------------------------------------------------------------- helpers

    internal static Vector3 Div(Vector3 a, Vector3 b) =>
        new(b.x == 0 ? a.x : a.x / b.x, b.y == 0 ? a.y : a.y / b.y, b.z == 0 ? a.z : a.z / b.z);

    /// <summary>Path of <paramref name="t"/> below <paramref name="root"/> ("" for the root itself, null if outside).</summary>
    internal static string Path(Transform root, Transform t)
    {
        if (t == null) return null;
        var parts = new List<string>();
        for (var cur = t; cur != null; cur = cur.parent)
        {
            if (cur == root) { parts.Reverse(); return string.Join("/", parts); }
            parts.Add(cur.name);
        }
        return null;
    }

    internal static Transform Find(Transform root, string path) => path == "" ? root : root.Find(path);

    private static string Dump(Transform root)
    {
        var sb = new StringBuilder();
        int lines = 0;
        void Walk(Transform t, int depth)
        {
            if (lines++ > 300) { if (lines == 302) sb.Append("\n  ..."); return; }
            var comps = t.GetComponents<Component>();
            var names = new List<string>();
            bool canvas = false;
            for (int i = 0; i < comps.Length; i++)
            {
                var n = comps[i]?.GetIl2CppType().Name ?? "?";
                if (n == "Transform") continue;
                if (n == "Canvas") canvas = true;
                names.Add(n);
            }
            sb.Append('\n').Append(' ', 2 + depth * 2).Append(t.name)
              .Append(" [").Append(string.Join(",", names)).Append("] layer=").Append(t.gameObject.layer)
              .Append(t.gameObject.activeSelf ? "" : " (inactive)");
            if (canvas) { sb.Append(" (UI subtree not listed)"); return; }
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1);
        }
        Walk(root, 0);
        return sb.ToString();
    }

    /// <summary>Called when the game scene goes away: forget scene objects, keep the template.</summary>
    internal static void OnSceneObjectsGone()
    {
        _computer = null;
        ShopItem = null;
    }
}
