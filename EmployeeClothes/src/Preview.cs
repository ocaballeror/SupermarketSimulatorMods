using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EmployeeClothes;

/// <summary>
/// Renders the models of one employee type into a RenderTexture for the panel. A camera and two point lights sit far
/// below the map (y = -5000) on an unnamed layer; the models are clones of the prefabs' model sub-trees (the top
/// Animator's GameObject), standing in a row and turning slowly. URP ignores a light's culling mask, which is why the
/// lights are point lights with a limited range instead of a directional light: they can't light the store.
/// </summary>
internal static class Preview
{
    private const float Spacing = 1.15f;
    private const int Width = 1024, Height = 512;

    private sealed class Entry
    {
        public GameObject Pivot;
        public GameObject Model;
    }

    private static GameObject _root;
    private static Camera _cam;
    private static Light _light1, _light2;
    private static RenderTexture _rt;
    private static int _layer = 31;
    private static readonly List<Entry> Models = new();
    private static Color? _color;
    private static bool _needApply;
    private static bool _shown;
    private static float _yaw;
    private static bool _loggedFirstFrame;

    internal static RenderTexture Texture => _rt;
    internal static int ModelCount => Models.Count;

    // ------------------------------------------------------------------ the stage

    private static int FreeLayer()
    {
        for (int i = 31; i >= 8; i--)
            if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
        return 31;
    }

    private static bool EnsureStage()
    {
        if (_root != null && _cam != null && _rt != null) return true;
        DestroyStage();
        try
        {
            _layer = FreeLayer();
            _root = new GameObject("EmployeeClothes.PreviewStage");
            _root.transform.position = new Vector3(0f, -5000f, 0f);

            _rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            _rt.name = "EmployeeClothes.Preview";
            _rt.Create();

            var camGo = new GameObject("EmployeeClothes.PreviewCamera");
            camGo.transform.SetParent(_root.transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.targetTexture = _rt;
            _cam.cullingMask = 1 << _layer;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
            _cam.orthographic = true;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 40f;
            _cam.depth = -50f;
            _cam.allowHDR = false;
            _cam.allowMSAA = false;
            _cam.useOcclusionCulling = false;

            _light1 = MakeLight("EmployeeClothes.PreviewLight1", new Vector3(-1.8f, 3.2f, 3.5f));
            _light2 = MakeLight("EmployeeClothes.PreviewLight2", new Vector3(2.2f, 2.0f, 3.0f));
            ApplyLightSetting();
            Plugin.Debug($"Preview stage built (layer {_layer}, render texture {Width}x{Height})");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Error("Building the preview stage failed: " + e);
            DestroyStage();
            return false;
        }
    }

    private static Light MakeLight(string name, Vector3 localPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = localPos;
        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.range = 16f;
        l.color = Color.white;
        l.shadows = LightShadows.None;
        return l;
    }

    internal static void ApplyLightSetting()
    {
        float v = Plugin.PreviewLight?.Value ?? 30f;
        if (_light1 != null) _light1.intensity = v;
        if (_light2 != null) _light2.intensity = v * 0.7f;
    }

    // ------------------------------------------------------------------ models

    private static void AddPrefab(List<GameObject> list, HashSet<IntPtr> seen, Component prefab)
    {
        if (prefab == null) return;
        var go = prefab.gameObject;
        if (go != null && seen.Add(go.Pointer)) list.Add(go);
    }

    /// <summary>The scene-asset prefab of every employee of this type (the ScriptableObjects the HR screen lists).</summary>
    private static List<GameObject> PrefabsOf(HrEmployeeKind kind)
    {
        var list = new List<GameObject>();
        if (!NoktaSingleton<IDManager>.HasInstance) return list;
        var ids = NoktaSingleton<IDManager>.Instance;
        var seen = new HashSet<IntPtr>();
        switch (kind)
        {
            case HrEmployeeKind.Cashier:
                for (int i = 0; i < ids.m_Cashiers.Count; i++) AddPrefab(list, seen, ids.m_Cashiers[i]?.CashierPrefab);
                break;
            case HrEmployeeKind.Restocker:
                for (int i = 0; i < ids.m_Restockers.Count; i++) AddPrefab(list, seen, ids.m_Restockers[i]?.RestockerPrefab);
                break;
            case HrEmployeeKind.CustomerHelper:
                for (int i = 0; i < ids.m_CustomerHelpers.Count; i++) AddPrefab(list, seen, ids.m_CustomerHelpers[i]?.CustomerHelperPrefab);
                break;
            case HrEmployeeKind.SecurityGuard:
                for (int i = 0; i < ids.m_SecurityGuards.Count; i++) AddPrefab(list, seen, ids.m_SecurityGuards[i]?.SecurityGuardPrefab);
                break;
            case HrEmployeeKind.Janitor:
                for (int i = 0; i < ids.m_Janitor.Count; i++) AddPrefab(list, seen, ids.m_Janitor[i]?.JanitorPrefab);
                break;
            case HrEmployeeKind.Baker:
                for (int i = 0; i < ids.m_Bakers.Count; i++) AddPrefab(list, seen, ids.m_Bakers[i]?.BakerPrefab);
                break;
            case HrEmployeeKind.IceCreamHelper:
                for (int i = 0; i < ids.m_IceCreamHelpers.Count; i++) AddPrefab(list, seen, ids.m_IceCreamHelpers[i]?.EmployeePrefab);
                break;
            case HrEmployeeKind.Courier:
                for (int i = 0; i < ids.m_Couriers.Count; i++) AddPrefab(list, seen, ids.m_Couriers[i]?.CourierPrefab);
                break;
        }
        return list;
    }

    private static void SetLayer(GameObject go, int layer)
    {
        var ts = go.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < ts.Length; i++) ts[i].gameObject.layer = layer;
    }

    /// <summary>Shows the models of this type (with the given colour, or the original clothes when null).</summary>
    internal static bool Show(HrEmployeeKind kind, Color? color)
    {
        if (!EnsureStage()) return false;
        ClearModels();
        _color = color;
        try
        {
            var prefabs = PrefabsOf(kind);
            foreach (var prefab in prefabs)
            {
                var anim = prefab.GetComponentInChildren<Animator>(true);
                var src = anim != null ? anim.gameObject : null;
                if (src == null) { Plugin.Debug($"Prefab '{prefab.name}' has no Animator; skipped in the preview"); continue; }

                var pivot = new GameObject("Pivot " + prefab.name);
                pivot.transform.SetParent(_root.transform, false);
                var clone = Object.Instantiate(src, pivot.transform);
                clone.name = prefab.name + " (preview)";
                clone.SetActive(true);
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = src.transform.localRotation;
                clone.transform.localScale = Vector3.Scale(src.transform.localScale, prefab.transform.localScale);
                SetLayer(pivot, _layer);

                var animators = clone.GetComponentsInChildren<Animator>(true);
                for (int i = 0; i < animators.Length; i++)
                {
                    animators[i].applyRootMotion = false;
                    animators[i].cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }
                var lods = clone.GetComponentsInChildren<LODGroup>(true);
                for (int i = 0; i < lods.Length; i++) lods[i].ForceLOD(0);

                Models.Add(new Entry { Pivot = pivot, Model = clone });
            }

            int n = Models.Count;
            for (int i = 0; i < n; i++)
                Models[i].Pivot.transform.localPosition = new Vector3((i - (n - 1) / 2f) * Spacing, 0f, 0f);

            FrameCamera();
            _yaw = 0f;
            _shown = true;
            _cam.enabled = true;
            _needApply = true;
            Plugin.Debug($"Preview: {n} model(s) for {ClothesStore.NameOf(kind)} ({prefabs.Count} prefab(s))");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Error("Building the preview models failed: " + e);
            ClearModels();
            return false;
        }
    }

    private static void FrameCamera()
    {
        int n = Math.Max(Models.Count, 1);
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < Models.Count; i++)
        {
            var rends = Models[i].Model.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < rends.Length; r++)
            {
                if (rends[r] == null) continue;
                var b = rends[r].bounds;
                if (b.size.y <= 0.01f || b.size.y > 6f) continue;
                minY = Math.Min(minY, b.min.y);
                maxY = Math.Max(maxY, b.max.y);
            }
        }
        float baseY = _root.transform.position.y;
        if (minY > maxY || maxY - minY < 0.6f) { minY = baseY; maxY = baseY + 1.8f; } // bounds not usable: assume a person
        float h = maxY - minY;
        float aspect = Width / (float)Height;
        float halfH = h * 0.58f;
        float halfW = n * Spacing * 0.54f / aspect;
        _cam.orthographicSize = Math.Max(halfH, halfW);
        var center = new Vector3(_root.transform.position.x, (minY + maxY) * 0.5f, _root.transform.position.z);
        var camPos = center + new Vector3(0f, 0.15f, 8f);
        _cam.transform.position = camPos;
        _cam.transform.rotation = Quaternion.LookRotation(center - camPos);
        Plugin.Debug($"Preview camera: height {h:0.00} m, ortho size {_cam.orthographicSize:0.00}");
    }

    private static void ClearModels()
    {
        foreach (var e in Models)
            if (e.Pivot != null) Object.Destroy(e.Pivot);
        Models.Clear();
    }

    // ------------------------------------------------------------------ per frame

    internal static void SetColor(Color? c)
    {
        _color = c;
        _needApply = true;
    }

    internal static void Update(float dt)
    {
        if (!_shown) return;
        if (_cam == null || _root == null) { _shown = false; return; }
        _yaw += 24f * dt;
        for (int i = 0; i < Models.Count; i++)
            if (Models[i].Pivot != null)
                Models[i].Pivot.transform.localRotation = Quaternion.Euler(0f, _yaw + i * 37f, 0f);

        if (_needApply)
        {
            Recolor.BeginBudget(12);
            bool complete = true;
            for (int i = 0; i < Models.Count; i++)
                if (Models[i].Model != null && !Recolor.ApplyToObject(Models[i].Model, Recolor.PreviewSlot, _color)) complete = false;
            _needApply = !complete;
        }
        if (!_loggedFirstFrame && Models.Count > 0)
        {
            _loggedFirstFrame = true;
            Plugin.Info($"Preview is running ({Models.Count} model(s), camera enabled {_cam.enabled}, render texture {(_rt != null ? _rt.width + "x" + _rt.height : "none")})");
        }
    }

    /// <summary>The material copies are about to be destroyed: show the original materials until they are rebuilt.</summary>
    internal static void OnMaterialsReset()
    {
        for (int i = 0; i < Models.Count; i++)
            if (Models[i].Model != null) Recolor.ApplyToObject(Models[i].Model, Recolor.PreviewSlot, null);
        _needApply = true;
    }

    internal static void Hide()
    {
        _shown = false;
        if (_cam != null) _cam.enabled = false;
        ClearModels();
    }

    private static void DestroyStage()
    {
        _shown = false;
        ClearModels();
        if (_root != null) Object.Destroy(_root);
        if (_rt != null) { _rt.Release(); Object.Destroy(_rt); }
        _root = null; _cam = null; _light1 = null; _light2 = null; _rt = null;
    }

    /// <summary>The mod was switched off: remove everything.</summary>
    internal static void Destroy() => DestroyStage();

    /// <summary>A new scene was loaded: the stage objects are gone with the old scene; free the render texture.</summary>
    internal static void Forget()
    {
        Models.Clear();
        _shown = false;
        if (_rt != null) { try { _rt.Release(); Object.Destroy(_rt); } catch (Exception) { } }
        _root = null; _cam = null; _light1 = null; _light2 = null; _rt = null;
    }
}
