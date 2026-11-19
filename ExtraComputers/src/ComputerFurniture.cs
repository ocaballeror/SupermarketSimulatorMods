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
 *    of the store computer's root. That root already has the layer 6 colliders that the interaction raycast hits,
 *    a NavMeshObstacle, Highlightable, snap points, and a "Placing Mode" child with its FurniturePlacingMode,
 *    whose references Instantiate remaps into the copy. It has NO network components: no PhotonView and no
 *    transform view (verified in the shipped scene level3, and in the host's log hierarchy dump; the parent
 *    "---GAME---" only carries EditorObjectTransformer). So the PhotonView and PhotonTransformViewOptimized are
 *    added to the copy's root with the settings of a real furniture prefab (SetUpNetworkViews). The PC camera,
 *    the screen UI canvases and every computer script (Computer, ComputerOperatingSystem, MusicPlayerManager,
 *    NetworkComputer, NetworkVendingMachineTab, ...) are removed; the placing mode's interaction-zone canvas is
 *    kept. Furniture + NetworkFurniture are added (plain Furniture + a type script is how the game's own
 *    Trash is set up).
 *    Before v1.2.0 the template was built on a "donor" furniture prefab; no furniture qualified in-game
 *    ("no usable furniture prefab", 2026-11-15), so nothing could be spawned and opening a computer box left
 *    the player in an empty placing mode. v1.2.0-1.2.2 assumed the store computer's root carried the PhotonView
 *    and failed with "the store computer has no PhotonView on its root" (2026-11-16).
 *    The template is registered in Photon's DefaultPool.ResourceCache under PrefabId.
 *  - The icon: the game's flat computer symbol (icon_device_63), falling back to a render of the model, then a drawn
 *    monitor. The game ships no computer icon.
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
    /// <summary>What BuildTemplate was doing when it threw (logged by EnsureTemplate).</summary>
    private static string _buildStep = "";
    private static GameObject _partial;
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
                if (_holder != null && c.transform.IsChildOf(_holder.transform)) continue; // never our template
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

    /// <summary>The computer's user side (towards the PC camera, i.e. where the player stands) as a horizontal
    /// direction in the furniture's local space. The store computer's camera and exit point are on its -Z side, which
    /// is the fallback when the layout hasn't been measured.</summary>
    internal static Vector3 UserSideLocal()
    {
        if (HaveFrontDirection)
        {
            var d = CamLocalPos - ScreenLocalPos;
            d.y = 0;
            if (d.sqrMagnitude > 1e-4f) return d.normalized;
        }
        return Vector3.back;
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
            Plugin.Debug($"Computer furniture registered: id {FurnitureId}, box {So.BoxSize}, price {So.Cost} " +
                        $"(definition copied from id {donor.ID})");
            if (Plugin.VerboseLogging.Value) LogFurnitureList(list);
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

    /// <summary>Called when the mod is switched on while the game runs (never from Load): creates the definition and
    /// the prefab if this scene was loaded without them, so the mod counts as ready for other players. The shop
    /// entry itself is only created by the next store load. Does nothing outside a loaded game (no IDManager).</summary>
    internal static void EnsureForLoadedScene()
    {
        if (Ids == null) return;
        EnsureSo();
        EnsureTemplate();
        RefreshShopItem();
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
        bool show = Plugin.Active && Template != null && !Presence.CachedBlocked;
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
        _buildStep = "start";
        _partial = null;
        try
        {
            BuildTemplate(comp, ids);
        }
        catch (Exception e)
        {
            Plugin.Error($"Building the computer furniture failed (attempt {_buildAttempts} of 3) at step '{_buildStep}': {e}");
            try
            {
                // What the copy looked like when it broke, so the next log shows which check failed and why.
                if (_partial != null) Plugin.Error("The computer copy at that point:" + Dump(_partial.transform));
            }
            catch (Exception e2) { Plugin.Warn("Couldn't dump the partial computer copy: " + e2.Message); }
            if (_holder != null) Object.DestroyImmediate(_holder);
            _holder = null;
            _partial = null;
            Template = null;
        }
        if (Template != null)
        {
            RegisterWithPhoton();
            if (So != null) So.FurniturePrefab = Template;
        }
        return Template != null;
    }

    /// <summary>Makes the template network-instantiable. Returns false if that isn't possible.</summary>
    internal static bool RegisterWithPhoton()
    {
        if (Template == null) return false;
        try
        {
            var pool = PhotonNetwork.PrefabPool;
            var dp = pool?.TryCast<DefaultPool>();
            if (dp == null)
            {
                Plugin.Error("Photon's prefab pool isn't the default pool; extra computers can't be placed online.");
                return false;
            }
            dp.ResourceCache[PrefabId] = Template;
            return true;
        }
        catch (Exception e) { Plugin.Error("Registering the computer with Photon failed: " + e.Message); return false; }
    }

    private static void BuildTemplate(Computer comp, IDManager ids)
    {
        // The transform the game moves when the computer itself is moved is the computer's root.
        var compPm = comp.m_PlacingMode;
        Transform compRoot = ComputerRoot(comp);

        if (Plugin.VerboseLogging.Value)
            Plugin.Debug("Store computer hierarchy:\n" + Dump(compRoot) +
                         $"\n  PCCam={Path(compRoot, comp.m_PCCam?.transform)} Screen={Path(compRoot, comp.m_ComputerScreen?.transform)} " +
                         $"PlayerPosition={Path(compRoot, comp.m_PlayerPosition)} PlacingMode={Path(compRoot, compPm?.transform)} " +
                         $"ZoneCanvas={Path(compRoot, compPm?.m_InteractionZoneCanvas?.transform)}");
        _buildStep = "checking the store computer's placing mode";
        if (compPm == null) throw new Exception("the store computer has no FurniturePlacingMode");

        string camPath = comp.m_PCCam != null ? Path(compRoot, comp.m_PCCam.transform) : null;
        string screenPath = comp.m_ComputerScreen != null ? Path(compRoot, comp.m_ComputerScreen.transform) : null;
        string zonePath = compPm.m_InteractionZoneCanvas != null ? Path(compRoot, compPm.m_InteractionZoneCanvas.transform) : null;
        MeasureFront(comp, compRoot);

        _buildStep = "copying the store computer";
        _holder = new GameObject("ExtraComputers.TemplateHolder");
        _holder.SetActive(false);
        Object.DontDestroyOnLoad(_holder);

        // A copy of the store computer. It is created under the inactive holder, so none of its scripts wake up.
        // It already has the physical setup of a movable object: the root (layer, colliders, navmesh obstacle,
        // highlight, snap points) and a FurniturePlacingMode whose references (m_Furniture, renderers, colliders)
        // Instantiate has remapped into this copy. It has no network components (no PhotonView, no transform view);
        // SetUpNetworkViews adds them below and m_ParentPhotonView is pointed at the new view.
        var tpl = Object.Instantiate(compRoot.gameObject, _holder.transform);
        _partial = tpl;
        tpl.name = "ExtraComputer";
        tpl.transform.localPosition = Vector3.zero;
        tpl.transform.localRotation = Quaternion.identity;
        // Unboxing forces the spawned furniture's scale to 1 (OpenBox tweens to 1, OpenBoxOrder sets Vector3.one), so a
        // store computer that isn't at unit scale would come out a different size.
        tpl.transform.localScale = Vector3.one;
        var sc = compRoot.lossyScale;
        if (Mathf.Abs(sc.x - 1f) > 0.01f || Mathf.Abs(sc.y - 1f) > 0.01f || Mathf.Abs(sc.z - 1f) > 0.01f)
            Plugin.Warn($"The store computer's scale is {sc}; extra computers will be built at scale 1 and may look a different size.");

        _buildStep = "stripping the computer UI from the copy";
        var zone = zonePath != null ? Find(tpl.transform, zonePath) : null;
        StripComputerCopy(tpl.transform, camPath, screenPath, zone);

        _buildStep = "finding the placing settings in the copy";
        var tplPm = Find(tpl.transform, Path(compRoot, compPm.transform))?.GetComponent<FurniturePlacingMode>();
        if (tplPm == null) throw new Exception("the placing settings weren't found in the computer copy");
        tplPm.m_Furniture = tpl;

        // The store computer has no PhotonView or transform view of its own (see SetUpNetworkViews), so they are added.
        _buildStep = "adding the network components";
        var view = SetUpNetworkViews(tpl, ids);
        tplPm.m_ParentPhotonView = view;

        // The furniture scripts, which the store computer doesn't have.
        _buildStep = "adding Furniture and NetworkFurniture";
        var furniture = tpl.AddComponent<Furniture>();
        var net = tpl.AddComponent<NetworkFurniture>();
        if (furniture == null || net == null)
            throw new Exception($"AddComponent returned null (Furniture {(furniture == null ? "null" : "ok")}, NetworkFurniture {(net == null ? "null" : "ok")})");
        furniture.m_ID = FurnitureId;
        furniture.m_PlacingMode = tplPm;
        furniture.m_PhotonView = view;
        furniture.m_NetworkFurniture = net;
        net.m_PhotonView = view;
        net.m_Furniture = furniture;

        _buildStep = "finishing";
        Template = tpl;
        _partial = null;
        Plugin.Debug("Computer furniture prefab built from a copy of the store computer");
        Plugin.Debug($"Placing settings: tags [{string.Join(",", Strings(tplPm.m_PlacingTag))}], " +
                     $"renderers {tplPm.m_Renderers?.Length ?? 0}, owner colliders {tplPm.m_OwnerCollider?.Count ?? 0}, " +
                     $"placing box {(tplPm.m_PlacingModeCollider == null ? "none" : tplPm.m_PlacingModeCollider.name)}, " +
                     $"zone canvas {(tplPm.m_InteractionZoneCanvas == null ? "none" : tplPm.m_InteractionZoneCanvas.name)}");
        if (Plugin.VerboseLogging.Value) Plugin.Debug("Computer furniture prefab:\n" + Dump(tpl.transform));
    }

    /// <summary>
    /// The network setup of the copy. The store computer is a scene object without any PhotonView of its own (checked
    /// in the shipped scene, level3: the root has Computer, NetworkComputer, NetworkVendingMachineTab, ... but no
    /// PhotonView, and its only parent "---GAME---" carries a single script, EditorObjectTransformer), so there is
    /// nothing to keep: a PhotonView and a PhotonTransformViewOptimized are added to the root, with the settings of
    /// a real furniture prefab (every furniture in resources.assets has exactly this pair on its root, plus its
    /// Network* script). The holder is inactive, so none of the added scripts runs Awake here; they wake up in the
    /// clones.
    ///  - PhotonView: OwnershipTransfer, Synchronization and observableSearch copied from the reference. The copy
    ///    gets no scene view id (0), so an offline LeanPool.Spawn clone can't claim a view id.
    ///    NetworkFurniture.OnPhotonInstantiate transfers ownership to the unboxing player, which Fixed would ignore.
    ///  - PhotonTransformViewOptimized: m_SyncEnabled, m_SynchronizePosition, m_SynchronizeRotation, m_UseLocal,
    ///    m_SendDelta and multiplier copied from the reference. The place RPCs of plain furniture carry no position,
    ///    so a moved computer reaches the other players only through this view's stream. A new component starts
    ///    with the constructor defaults (sync on, position and rotation, delta 0.01, multiplier 100); the prefabs
    ///    serialize their own values, so those are copied rather than assumed.
    ///  - Only the transform view is put in the view's ObservedComponents (the view's Awake refills that list itself
    ///    when observableSearch isn't Manual, and would find the transform view too).
    /// NetworkComputer and NetworkVendingMachineTab (which would otherwise resolve photonView to this view, and whose
    /// RPCs would then target the clone's view id) are stripped from the copy; a surviving script fails the build.
    /// Returns the PhotonView.
    /// </summary>
    private static readonly string[] ResourceFolders = { "Trash", "Vendings", "Displays", "Racks", "Checkouts" };

    private static PhotonView SetUpNetworkViews(GameObject tpl, IDManager ids)
    {
        // A reference furniture prefab: root PhotonView + PhotonTransformViewOptimized, preferably also Furniture and
        // NetworkFurniture (Trash is one), since our computer has those two as well. FurnitureSO.FurniturePrefab points
        // at the plain scene-asset prefab, which has NO network components (checked in sharedassets2: Trash there is
        // Trash + Furniture + TrashBin ... without PhotonView); the networked prefabs are separate assets in
        // Resources ("Furnitures/<folder>/<name>", the paths NetworkObjectGenerator builds, resources.assets). That is
        // why looking at FurniturePrefab alone (v1.2.0 donor search) found nothing.
        GameObject reference = null;
        string referenceFrom = null;
        int bestScore = -1;
        var seen = new List<string>();
        var list = ids.m_Furnitures;
        for (int i = 0; list != null && i < list.Count; i++)
        {
            var s = list[i];
            if (s == null || s.ID == FurnitureId) continue;
            var plain = s.FurniturePrefab;
            if (plain == null) { seen.Add($"{s.ID}: <no prefab>"); continue; }
            var candidates = new List<(GameObject go, string from)> { (plain, "FurniturePrefab") };
            foreach (var folder in ResourceFolders)
            {
                GameObject loaded = null;
                try { loaded = Resources.Load<GameObject>($"Furnitures/{folder}/{plain.name}"); }
                catch (Exception e) { Plugin.Debug($"Resources.Load Furnitures/{folder}/{plain.name} threw: {e.Message}"); }
                if (loaded != null) { candidates.Add((loaded, $"Resources Furnitures/{folder}/{plain.name}")); break; }
            }
            foreach (var (go, from) in candidates)
            {
                bool hasView = go.GetComponent<PhotonView>() != null;
                bool hasTv = go.GetComponent<PhotonTransformViewOptimized>() != null;
                bool hasF = go.GetComponent<Furniture>() != null;
                bool hasNf = go.GetComponent<NetworkFurniture>() != null;
                seen.Add($"{s.ID} {from}: PhotonView {hasView}, PhotonTransformViewOptimized {hasTv}, Furniture {hasF}, NetworkFurniture {hasNf}");
                if (!hasView || !hasTv) continue;
                int score = 1 + (hasF ? 1 : 0) + (hasNf ? 1 : 0);
                if (score > bestScore) { bestScore = score; reference = go; referenceFrom = $"{s.ID} {from}"; }
            }
        }
        PhotonView refView = reference != null ? reference.GetComponent<PhotonView>() : null;
        var refTv = reference != null ? reference.GetComponent<PhotonTransformViewOptimized>() : null;
        if (reference == null)
            Plugin.Warn("No furniture prefab with a PhotonView and a PhotonTransformViewOptimized was found to copy the " +
                        "network settings from; using the defaults. Candidates: " + string.Join("; ", seen));
        else
            Plugin.Debug($"Network settings are copied from '{reference.name}' ({referenceFrom}); candidates: " + string.Join("; ", seen));

        var view = tpl.GetComponent<PhotonView>();
        bool addedView = view == null;
        if (addedView) view = tpl.AddComponent<PhotonView>();
        if (view == null) throw new Exception("AddComponent<PhotonView>() returned null");
        var tv = tpl.GetComponent<PhotonTransformViewOptimized>();
        bool addedTv = tv == null;
        if (addedTv) tv = tpl.AddComponent<PhotonTransformViewOptimized>();
        if (tv == null) throw new Exception("AddComponent<PhotonTransformViewOptimized>() returned null");

        view.sceneViewId = 0;
        if (refView != null)
        {
            view.OwnershipTransfer = refView.OwnershipTransfer;
            view.Synchronization = refView.Synchronization;
            view.observableSearch = refView.observableSearch;
        }
        else
        {
            view.OwnershipTransfer = OwnershipOption.Takeover;
            view.Synchronization = ViewSynchronization.UnreliableOnChange;
            view.observableSearch = PhotonView.ObservableSearch.AutoFindAll;
        }
        if (view.OwnershipTransfer == OwnershipOption.Fixed) view.OwnershipTransfer = OwnershipOption.Takeover;

        if (refTv != null)
        {
            tv.m_SyncEnabled = refTv.m_SyncEnabled;
            tv.m_SynchronizePosition = refTv.m_SynchronizePosition;
            tv.m_SynchronizeRotation = refTv.m_SynchronizeRotation;
            tv.m_UseLocal = refTv.m_UseLocal;
            tv.m_SendDelta = refTv.m_SendDelta;
            if (refTv.multiplier > 0f) tv.multiplier = refTv.multiplier; // the stream divides by it
        }
        // A transform view that doesn't sync would leave a moved computer where it was spawned for everyone else.
        if (!tv.m_SyncEnabled || (!tv.m_SynchronizePosition && !tv.m_SynchronizeRotation))
        {
            Plugin.Warn($"The reference transform view doesn't sync (enabled {tv.m_SyncEnabled}, position {tv.m_SynchronizePosition}, " +
                        $"rotation {tv.m_SynchronizeRotation}); turning position and rotation sync on for the computer.");
            tv.m_SyncEnabled = true;
            tv.m_SynchronizePosition = true;
            tv.m_SynchronizeRotation = true;
        }

        var observed = new Il2CppSystem.Collections.Generic.List<Component>();
        observed.Add(tv);
        view.ObservedComponents = observed;

        Plugin.Debug($"PhotonView (added {addedView}): ownership {view.OwnershipTransfer}, sync {view.Synchronization}, " +
                     $"search {view.observableSearch}, observes [{string.Join(",", ObservedNames(view))}]; " +
                     $"transform view (added {addedTv}): sync {tv.m_SyncEnabled}, position {tv.m_SynchronizePosition}, " +
                     $"rotation {tv.m_SynchronizeRotation}, local {tv.m_UseLocal}, delta {tv.m_SendDelta}, multiplier {tv.multiplier}");
        return view;
    }

    private static List<string> ObservedNames(PhotonView view)
    {
        var names = new List<string>();
        var obs = view.ObservedComponents;
        for (int i = 0; obs != null && i < obs.Count; i++)
            names.Add(obs[i] == null ? "<null>" : obs[i].GetIl2CppType().Name);
        return names;
    }

    private static readonly HashSet<string> KeptModelComponents = new()
    {
        "Transform", "RectTransform", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "LODGroup",
        "BoxCollider", "MeshCollider", "SphereCollider", "CapsuleCollider", "Rigidbody",
        "NavMeshObstacle", "NavmeshBuildSourceObject", "FurniturePlacingMode", "Highlightable",
        "FurnitureSnapPointSetup", "FurnitureSnapPoint",
        // The store computer has none of these (SetUpNetworkViews adds them after stripping); listed so a future
        // game version that does give it a view isn't stripped.
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
                Object.DestroyImmediate(c);
                // Unity refuses (with a log line only, no exception) to destroy a component another one requires.
                if (c == null) removed.Add(name);
            }
        }
        if (removed.Count > 0)
            Plugin.Debug("Removed from the computer copy: " + string.Join(", ", removed.Distinct()));

        // Whatever is left would come along on every computer. A game script would run its Awake there.
        var survivors = new List<string>();
        bool scriptSurvived = false;
        var left = model.GetComponentsInChildren<Component>(true);
        for (int i = 0; i < left.Length; i++)
        {
            var c = left[i];
            if (c == null || (keep != null && c.transform.IsChildOf(keep))) continue;
            var name = c.GetIl2CppType().Name;
            if (KeptModelComponents.Contains(name)) continue;
            survivors.Add($"{name} on '{c.gameObject.name}'");
            if (c.TryCast<MonoBehaviour>() != null) scriptSurvived = true;
        }
        if (survivors.Count == 0) return;
        Plugin.Error("Couldn't remove from the computer copy: " + string.Join(", ", survivors.Distinct()));
        if (scriptSurvived) throw new Exception("a game script couldn't be removed from the computer copy (see the line above)");
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

    /// <summary>The furniture shop's icons are renders in one atlas (T_IconAtlas_Furnitures), which has no computer.
    /// Order of preference: the game's flat white computer symbol (sprite icon_device_63, a UI glyph), then
    /// an icon rendered from the computer's model in the same style as the atlas, then a
    /// drawn monitor. The drawn one is only a stopgap: rendering is retried a few times, because the store computer
    /// may not exist yet when the shop first asks for the icon.</summary>
    internal static void EnsureIcon()
    {
        if (So == null) return;
        if (!_iconRendered && _iconRenderAttempts < 3)
        {
            _iconRenderAttempts++;
            Texture2D tex = null;
            tex = IconMaker.FromGameSymbol();
            if (tex == null)
            {
                Transform model = EnsureTemplate() ? Template.transform : null;
                if (model == null)
                {
                    var comp = FindRealComputer();
                    if (comp != null) { model = ComputerRoot(comp); MeasureFront(comp, model); }
                }
                if (model != null) tex = IconMaker.Render(model);
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
        if (!_iconRendered) Plugin.Debug("Computer icon: using the drawn placeholder for now");
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
            // Vanilla SetFurnitureIcon only sets the _BaseMap offset to AtlasPosition * 0.125 (ISIL); the 1/8 cell size
            // comes either from the material's tiling or from the label mesh's UVs. With atlas tiling s, the mesh UVs
            // span (1/8)/s, so a scale of 8*s makes our whole texture fill the label in both cases.
            foreach (var prop in new[] { "_BaseMap", "_MainTex" })
            {
                if (!_boxIconMaterial.HasProperty(prop)) continue;
                var atlasScale = mats[0].GetTextureScale(prop);
                var scale = atlasScale * 8f;
                _boxIconMaterial.SetTexture(prop, IconTexture);
                _boxIconMaterial.SetTextureScale(prop, scale);
                _boxIconMaterial.SetTextureOffset(prop, Vector2.zero);
                Plugin.Debug($"Box label material: {prop} atlas tiling {atlasScale}, ours {scale}");
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
