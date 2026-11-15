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
 *  - A prefab template, kept under an inactive DontDestroyOnLoad holder (so nothing in it ever wakes):
 *      root = copy of a simple "donor" furniture prefab (Furniture + NetworkFurniture + PhotonView +
 *             transform view + FurniturePlacingMode), with its own visuals, colliders and
 *             type-specific scripts removed;
 *      child "ExtraComputerModel" = copy of the store's real computer with every script, camera,
 *             UI canvas and network component removed (meshes, colliders, navmesh data kept).
 *    The donor's FurniturePlacingMode gets the real computer's placing settings (remapped to the copy).
 *    The root gets a box collider around the whole model (+1 cm per side) for interaction raycasts; it is
 *    listed in m_OwnerCollider (IPlacingMode.OverlapBoxes ignores those). m_PhysicalColliders is not read by
 *    any native code (only its own getter exists), so it is left as copied.
 *    The template is registered in Photon's DefaultPool.ResourceCache under PrefabId.
 *  - An icon rendered from the model (fallback: a drawn monitor icon).
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
    // The path the game's own spawner would use for a plain Furniture prefab called "ExtraComputer"
    // ("Furnitures/<component type>/<prefab name>"). Registered too, so a computer box can still be opened
    // by the vanilla code while the mod is switched off at runtime (best effort, not verified in-game).
    public const string VanillaPrefabId = "Furnitures/Furniture/ExtraComputer";
    public const string ModelName = "ExtraComputerModel";
    public const string ScreenMarker = "#ecscreen";
    public const string DisplayName = "Computer";

    internal static FurnitureSO So;
    internal static GameObject Template;
    /// <summary>The computer's entry in the furniture shop (built once per scene by FurnituresViewer.Start).</summary>
    internal static FurnitureSalesItem ShopItem;
    internal static Texture2D IconTexture;
    private static Sprite _iconSprite;
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
        RefreshShopItem();
    }

    /// <summary>Shows the shop entry only while the mod is on and every other player has it.</summary>
    internal static void RefreshShopItem()
    {
        var item = ShopItem;
        if (item == null) return;
        bool show = Plugin.Active && !Presence.Blocked();
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
            dp.ResourceCache[VanillaPrefabId] = Template;
        }
        catch (Exception e) { Plugin.Error("Registering the computer with Photon failed: " + e.Message); }
    }

    private static void BuildTemplate(Computer comp, IDManager ids)
    {
        // The transform the game moves when the computer itself is moved is the computer's root.
        var compPm = comp.m_PlacingMode;
        Transform compRoot = compPm != null && compPm.m_Furniture != null ? compPm.m_Furniture.transform : comp.transform;

        if (Plugin.Verbose.Value)
            Plugin.Debug("Store computer hierarchy:\n" + Dump(compRoot) +
                         $"\n  PCCam={Path(compRoot, comp.m_PCCam?.transform)} Screen={Path(compRoot, comp.m_ComputerScreen?.transform)} " +
                         $"PlayerPosition={Path(compRoot, comp.m_PlayerPosition)} PlacingMode={Path(compRoot, compPm?.transform)}");

        var donor = PickDonor(ids);
        if (donor == null) throw new Exception("no usable furniture prefab to copy the network setup from");
        Plugin.Info($"Using furniture id {donor.ID} ('{donor.FurniturePrefab.name}') as the base for the computer's network setup");
        if (Plugin.Verbose.Value) Plugin.Debug("Base furniture prefab:\n" + Dump(donor.FurniturePrefab.transform));

        _holder = new GameObject("ExtraComputers.TemplateHolder");
        _holder.SetActive(false);
        Object.DontDestroyOnLoad(_holder);

        var tpl = Object.Instantiate(donor.FurniturePrefab, _holder.transform);
        tpl.name = "ExtraComputer";
        tpl.transform.localPosition = Vector3.zero;
        tpl.transform.localRotation = Quaternion.identity;
        StripDonor(tpl);
        // The interaction raycast only hits layers in PlayerInteraction.m_InteractableLayer; the store
        // computer's own object is hit by it, so its layer is a safe choice for our root collider.
        Plugin.Debug($"Root layer: base furniture {tpl.layer}, store computer {comp.gameObject.layer}");
        tpl.layer = comp.gameObject.layer;

        // Copy of the real computer, as a child at identity.
        var model = Object.Instantiate(compRoot.gameObject, tpl.transform);
        model.name = ModelName;
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;
        model.transform.localScale = Div(compRoot.lossyScale, tpl.transform.lossyScale);

        string pmPath = compPm != null ? Path(compRoot, compPm.transform) : null;
        string camPath = comp.m_PCCam != null ? Path(compRoot, comp.m_PCCam.transform) : null;
        string screenPath = comp.m_ComputerScreen != null ? Path(compRoot, comp.m_ComputerScreen.transform) : null;
        if (comp.m_PCCam != null && comp.m_ComputerScreen != null)
        {
            CamLocalPos = compRoot.InverseTransformPoint(comp.m_PCCam.transform.position);
            ScreenLocalPos = compRoot.InverseTransformPoint(comp.m_ComputerScreen.transform.position);
            HaveFrontDirection = true;
        }

        var modelPm = pmPath != null ? Find(model.transform, pmPath)?.GetComponent<FurniturePlacingMode>() : null;
        StripComputerCopy(model.transform, camPath, screenPath);

        var furniture = tpl.GetComponent<Furniture>();
        var tplPm = furniture.m_PlacingMode;
        if (tplPm == null || !tplPm.transform.IsChildOf(tpl.transform)) tplPm = tpl.GetComponentInChildren<FurniturePlacingMode>(true);
        if (tplPm == null) throw new Exception("the base furniture has no FurniturePlacingMode");

        var rootBox = AddInteractionCollider(tpl, model.transform);
        if (modelPm != null)
        {
            CopyPlacingMode(modelPm, tplPm, tpl.transform, rootBox);
            Object.DestroyImmediate(modelPm);
        }
        else
        {
            Plugin.Warn("The store computer has no placing settings to copy; using the base furniture's.");
            FillPlacingModeFromModel(tplPm, tpl.transform, rootBox);
        }
        tplPm.m_Furniture = tpl;
        tplPm.m_ParentPhotonView = tpl.GetComponent<PhotonView>();

        furniture.m_ID = FurnitureId;
        furniture.m_PlacingMode = tplPm;

        Template = tpl;
        Plugin.Info("Computer furniture prefab built");
        if (Plugin.Verbose.Value) Plugin.Debug("Computer furniture prefab:\n" + Dump(tpl.transform));
    }

    private static readonly HashSet<string> SpecialFurnitureTypes = new()
    {
        "Rack", "Display", "Checkout", "CategorySign", "Scale", "IceCreamStand", "VendingMachine",
        "SelfCheckout", "Computer",
    };

    private static FurnitureSO PickDonor(IDManager ids)
    {
        var list = ids.m_Furnitures;
        FurnitureSO best = null;
        int bestScore = int.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s == null || s.ID == FurnitureId) continue;
            var p = s.FurniturePrefab;
            if (p == null) continue;
            var f = p.GetComponent<Furniture>();
            if (f == null || f.GetIl2CppType().Name != "Furniture") continue;
            if (p.GetComponent<NetworkFurniture>() == null || p.GetComponent<PhotonView>() == null) continue;
            if (p.GetComponentInChildren<FurniturePlacingMode>(true) == null) continue;

            int score = 0;
            bool special = false;
            var behaviours = p.GetComponentsInChildren<MonoBehaviour>(true);
            for (int b = 0; b < behaviours.Length; b++)
            {
                var name = behaviours[b]?.GetIl2CppType().Name;
                if (name == null) continue;
                if (SpecialFurnitureTypes.Contains(name)) { special = true; break; }
                if (!IsKeptDonorBehaviour(name)) score += 10;
            }
            if (special) continue;
            if (!s.IsMainFurniture) score += 1;
            if (score < bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    private static bool IsKeptDonorBehaviour(string name) =>
        name == "Furniture" || name == "NetworkFurniture" || name == "FurniturePlacingMode" || name.Contains("Photon");

    /// <summary>Removes the donor's look, colliders, navmesh data and type-specific scripts.</summary>
    private static void StripDonor(GameObject tpl)
    {
        DestroyCanvasSubtrees(tpl.transform);
        var removed = new List<string>();
        for (int pass = 0; pass < 3; pass++)
        {
            var comps = tpl.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                var name = c.GetIl2CppType().Name;
                if (name == "Transform" || name == "RectTransform" || name == "Rigidbody") continue;
                if (c.TryCast<MonoBehaviour>() != null && IsKeptDonorBehaviour(name)) continue;
                if (pass == 0 && c.TryCast<Behaviour>() == null && c.TryCast<Renderer>() == null) continue; // scripts first
                removed.Add(name);
                Object.DestroyImmediate(c);
            }
        }
        if (removed.Count > 0)
            Plugin.Debug("Removed from the base furniture copy: " + string.Join(", ", removed.Distinct()));
        var bodies = tpl.GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < bodies.Length; i++)
            Plugin.Debug($"Base furniture keeps a Rigidbody on '{bodies[i].name}': kinematic={bodies[i].isKinematic}, gravity={bodies[i].useGravity}");
    }

    private static readonly HashSet<string> KeptModelComponents = new()
    {
        "Transform", "RectTransform", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "LODGroup",
        "BoxCollider", "MeshCollider", "SphereCollider", "CapsuleCollider",
        "NavMeshObstacle", "NavmeshBuildSourceObject", "FurniturePlacingMode",
    };

    /// <summary>Removes the camera, the UI and every script from the computer copy.</summary>
    private static void StripComputerCopy(Transform model, string camPath, string screenPath)
    {
        if (camPath != null)
        {
            var cam = Find(model, camPath);
            if (cam != null && cam != model) Object.DestroyImmediate(cam.gameObject);
        }
        DestroyCanvasSubtrees(model);

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

    private static void DestroyCanvasSubtrees(Transform root)
    {
        var canvases = root.GetComponentsInChildren<Canvas>(true);
        var gos = new List<GameObject>();
        for (int i = 0; i < canvases.Length; i++)
            if (canvases[i] != null && canvases[i].transform != root) gos.Add(canvases[i].gameObject);
        foreach (var go in gos)
            if (go != null) Object.DestroyImmediate(go);
    }

    /// <summary>A box around the whole model on the furniture root, slightly larger than the model,
    /// so the interaction raycast always hits the root (where the Furniture component is) first.</summary>
    private static BoxCollider AddInteractionCollider(GameObject tpl, Transform model)
    {
        var b = ModelBounds(tpl.transform, model);
        var box = tpl.GetComponent<BoxCollider>() ?? tpl.AddComponent<BoxCollider>();
        box.isTrigger = false;
        box.center = b.center;
        box.size = b.size + new Vector3(0.02f, 0.02f, 0.02f);
        return box;
    }

    /// <summary>Bounds of all meshes under <paramref name="model"/>, in <paramref name="space"/>'s local space
    /// (works on inactive objects, unlike Renderer.bounds).</summary>
    internal static Bounds ModelBounds(Transform space, Transform model)
    {
        bool any = false;
        var b = new Bounds();
        var filters = model.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            var mf = filters[i];
            if (mf == null || mf.sharedMesh == null) continue;
            var mb = mf.sharedMesh.bounds;
            for (int k = 0; k < 8; k++)
            {
                var corner = mb.center + Vector3.Scale(mb.extents,
                    new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                var p = space.InverseTransformPoint(mf.transform.TransformPoint(corner));
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                else b.Encapsulate(p);
            }
        }
        if (!any) b = new Bounds(new Vector3(0, 0.5f, 0), Vector3.one);
        return b;
    }

    /// <summary>Gives the donor's placing component the real computer's settings (references already
    /// point into our copy because they were cloned together).</summary>
    private static void CopyPlacingMode(FurniturePlacingMode from, FurniturePlacingMode to, Transform tpl, BoxCollider rootBox)
    {
        to.m_PositionOffset = from.m_PositionOffset;
        to.m_RaycastIgnoresWalls = from.m_RaycastIgnoresWalls;
        to.m_OverrideAvailability = from.m_OverrideAvailability;
        to.m_PlacingTag = CopyStrings(from.m_PlacingTag);
        to.m_ExcludeTag = CopyStrings(from.m_ExcludeTag);

        to.m_Renderers = Keep(from.m_Renderers, tpl);
        to.m_PhysicalColliders = Keep(from.m_PhysicalColliders, tpl);
        to.m_NavMeshObstacles = Keep(from.m_NavMeshObstacles, tpl);
        to.m_OwnerCollider = KeepList(from.m_OwnerCollider, tpl);
        to.m_Triggers = KeepList(from.m_Triggers, tpl);
        to.m_PlacingModeCollider = Inside(from.m_PlacingModeCollider, tpl);
        to.m_PlacingModeCollider2 = Inside(from.m_PlacingModeCollider2, tpl);
        to.m_PlacingCollider = Inside(from.m_PlacingCollider, tpl);
        to.m_InteractionZoneCanvas = from.m_InteractionZoneCanvas != null && from.m_InteractionZoneCanvas.transform.IsChildOf(tpl)
            ? from.m_InteractionZoneCanvas : null;

        to.m_OwnerCollider ??= new Il2CppSystem.Collections.Generic.List<Collider>();
        to.m_OwnerCollider.Add(rootBox);

        if (to.m_Renderers == null || to.m_Renderers.Length == 0)
            to.m_Renderers = ModelRenderers(tpl);
        if (to.m_PlacingModeCollider == null)
            Plugin.Warn("The store computer's placing box wasn't found in the copy; overlap checks use the base furniture's settings.");

        Plugin.Debug($"Placing settings copied: tags [{string.Join(",", Strings(to.m_PlacingTag))}], " +
                     $"renderers {to.m_Renderers?.Length ?? 0}, owner colliders {to.m_OwnerCollider?.Count ?? 0}, " +
                     $"placing box {(to.m_PlacingModeCollider == null ? "none" : to.m_PlacingModeCollider.name)}, " +
                     $"offset {to.m_PositionOffset}");
    }

    private static void FillPlacingModeFromModel(FurniturePlacingMode pm, Transform tpl, BoxCollider rootBox)
    {
        pm.m_Renderers = ModelRenderers(tpl);
        pm.m_OwnerCollider = new Il2CppSystem.Collections.Generic.List<Collider>();
        var cols = tpl.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++) pm.m_OwnerCollider.Add(cols[i]);
        if (pm.m_PlacingModeCollider == null) pm.m_PlacingModeCollider = rootBox;
    }

    private static Il2CppReferenceArray<Renderer> ModelRenderers(Transform tpl)
    {
        var r = tpl.GetComponentsInChildren<Renderer>(true);
        var arr = new Il2CppReferenceArray<Renderer>(r.Length);
        for (int i = 0; i < r.Length; i++) arr[i] = r[i];
        return arr;
    }

    private static T Inside<T>(T c, Transform root) where T : Component =>
        c != null && c.transform.IsChildOf(root) ? c : null;

    private static Il2CppReferenceArray<T> Keep<T>(Il2CppReferenceArray<T> src, Transform root) where T : Component
    {
        if (src == null) return null;
        var kept = new List<T>();
        for (int i = 0; i < src.Length; i++)
            if (src[i] != null && src[i].transform.IsChildOf(root)) kept.Add(src[i]);
        var arr = new Il2CppReferenceArray<T>(kept.Count);
        for (int i = 0; i < kept.Count; i++) arr[i] = kept[i];
        return arr;
    }

    private static Il2CppSystem.Collections.Generic.List<T> KeepList<T>(Il2CppSystem.Collections.Generic.List<T> src, Transform root) where T : Component
    {
        if (src == null) return null;
        var list = new Il2CppSystem.Collections.Generic.List<T>();
        for (int i = 0; i < src.Count; i++)
            if (src[i] != null && src[i].transform.IsChildOf(root)) list.Add(src[i]);
        return list;
    }

    private static Il2CppSystem.Collections.Generic.List<string> CopyStrings(Il2CppSystem.Collections.Generic.List<string> src)
    {
        if (src == null) return null;
        var list = new Il2CppSystem.Collections.Generic.List<string>();
        for (int i = 0; i < src.Count; i++) list.Add(src[i]);
        return list;
    }

    private static IEnumerable<string> Strings(Il2CppSystem.Collections.Generic.List<string> l)
    {
        if (l == null) yield break;
        for (int i = 0; i < l.Count; i++) yield return l[i];
    }

    // ---------------------------------------------------------------- icon

    internal static void EnsureIcon()
    {
        if (So == null) return;
        if (IconTexture == null)
        {
            IconTexture = (EnsureTemplate() ? IconMaker.Render(Template.transform.Find(ModelName)) : null) ?? IconMaker.Draw();
            IconTexture.name = "ExtraComputerIcon";
            IconTexture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        }
        if (_iconSprite == null)
        {
            _iconSprite = Sprite.Create(IconTexture, new Rect(0, 0, IconTexture.width, IconTexture.height), new Vector2(0.5f, 0.5f), 100f);
            _iconSprite.name = "ExtraComputerIcon";
            _iconSprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        }
        if (So.FurnitureIcon == null || So.FurnitureIcon.Pointer != _iconSprite.Pointer) So.FurnitureIcon = _iconSprite;
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
