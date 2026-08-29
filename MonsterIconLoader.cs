using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.TargetLens;

/// <summary>
/// Resolves a monster's head-portrait texture (+ atlas UV sub-rect) from its MonsterTable config id, for the Target
/// HUD header. Fully self-contained and GUARDED: any missing type / null table / freed object degrades to a null icon
/// (the HUD header still renders name + HP without it) — never throws.
///
/// Chain (all release_3.7 dump.cs):
///   Bokura.MonsterTableBase.GetTable(false).TryGetValue(configId, out row, false) → row.ModelID (int)
///   Bokura.ModelTableBase.GetTable(false).TryGetValue(ModelID,  out row, false) → row.Image (string atlas path)
///   Panda.ZUi.ZImage.SetImage((ZResAddress)path)  [async UniTask → Image.sprite lands a few frames later]
///   Sprite.texture / Sprite.textureRect → normalized UvRect (bottom-left origin — no y-flip).
///
/// One reused ZImage on a hidden DontDestroyOnLoad GameObject drives the load; icons are cached by ModelID so a
/// re-seen monster is instant. Table objects are re-fetched each call (singletons that are null before the game
/// loads), so an early miss retries rather than latching a permanent no-icon (see Profession-Icon-Projection note).
/// The ZImage SetImage/sprite-poll path is invocation-UNVERIFIED in-game; the change-gated [MonIcon] diagnostic
/// (once per distinct ModelID) exists to confirm it on the next run.
/// </summary>
internal sealed class MonsterIconLoader
{
    private readonly IPluginServices _services;
    public MonsterIconLoader(IPluginServices services) => _services = services;

    // Resolved portraits, keyed by ModelID (not configId — many monsters share one model).
    private readonly Dictionary<int, (object tex, UvRect uv)> _cache = new();

    // In-flight load state: the single reused ZImage is currently awaiting the sprite for this ModelID.
    private int    _loadingModelId = -1;
    private string _loadingPath    = "";

    // Owned, hidden asset-loader GameObject (not under any Canvas — it just drives the async sprite load).
    private GameObject? _go;
    private object?     _zimg;      // ZImage wrapper — target for the SetImage reflection call
    private Image?      _img;       // same component cast to its Image base — for reading .sprite
    private bool        _goFailed;  // structural GO/AddComponent failure — stop retrying the GO build

    public object? GetIcon(int configId, out UvRect uv)
    {
        uv = default;
        try
        {
            if (configId <= 0) return null;
            if (!EnsureRefl()) return null;

            int modelId = ResolveModelId(configId);
            if (modelId <= 0) return null;

            // Cached — but a scene unload can free the Texture2D (fake-null); evict + reload if so.
            if (_cache.TryGetValue(modelId, out var hit))
            {
                if (Alive(hit.tex as UnityEngine.Object)) { uv = hit.uv; return hit.tex; }
                _cache.Remove(modelId);
                if (_loadingModelId == modelId) _loadingModelId = -1;
            }

            if (!EnsureGo()) return null;

            // Kick the async load for this model if we aren't already waiting on it.
            if (_loadingModelId != modelId)
            {
                string path = ResolveImagePath(modelId);
                if (string.IsNullOrEmpty(path)) { Diag(configId, modelId, path, false); return null; }
                if (!IssueLoad(path)) return null;
                _loadingModelId = modelId;
                _loadingPath    = path;
                Diag(configId, modelId, path, false);
                return null;   // sprite is null for a few frames
            }

            // Poll the sprite for the model we're loading.
            var sprite = _img != null ? _img.sprite : null;
            if (!Alive(sprite)) return null;                 // still loading (or freed)
            var tex = sprite!.texture;
            if (!Alive(tex)) return null;
            float tw = tex!.width, th = tex.height;
            if (tw <= 0f || th <= 0f) return null;

            var r  = sprite.textureRect;                     // pixel rect, bottom-left origin
            var nu = new UvRect(r.x / tw, r.y / th, r.width / tw, r.height / th);
            _cache[modelId] = (tex, nu);
            _loadingModelId = -1;
            Diag(configId, modelId, _loadingPath, true);
            uv = nu;
            return tex;
        }
        catch { return null; }
    }

    // ── config id → ModelID → atlas path ───────────────────────────────────────

    private int ResolveModelId(int configId)
    {
        try
        {
            var table = _monTblObj ??= _miMonGetTable!.Invoke(null, new object[] { false });
            if (table == null) { _monTblObj = null; return 0; }   // singleton not ready — retry next call
            if (_miMonTryGet == null) _miMonTryGet = FindTryGetValue(table);
            if (_miMonTryGet == null) return 0;

            var args = new object?[] { configId, null, false };
            if (_miMonTryGet.Invoke(table, args) is bool ok && ok && args[1] is object row)
                return _piModelId!.GetValue(row) is int m ? m : 0;
            return 0;
        }
        catch { _monTblObj = null; return 0; }
    }

    private string ResolveImagePath(int modelId)
    {
        try
        {
            var table = _modelTblObj ??= _miModelGetTable!.Invoke(null, new object[] { false });
            if (table == null) { _modelTblObj = null; return ""; }
            if (_miModelTryGet == null) _miModelTryGet = FindTryGetValue(table);
            if (_miModelTryGet == null) return "";

            var args = new object?[] { modelId, null, false };
            if (_miModelTryGet.Invoke(table, args) is bool ok && ok && args[1] is object row)
                return (_piImage!.GetValue(row) as string) ?? "";
            return "";
        }
        catch { _modelTblObj = null; return ""; }
    }

    private bool IssueLoad(string path)
    {
        try
        {
            var zres = _miOpImplicit!.Invoke(null, new object[] { path });
            if (zres == null) return false;
            _miSetImage!.Invoke(_zimg, new object[] { zres });
            return true;
        }
        catch (Exception ex) { WarnOnce($"[MonIcon] SetImage: {ex.InnerException?.Message ?? ex.Message}"); return false; }
    }

    // ── hidden loader GameObject ───────────────────────────────────────────────

    private bool EnsureGo()
    {
        if (_goFailed) return false;
        if (Alive(_go) && _img != null && _zimg != null) return true;

        // First build, or rebuild after a scene unload freed it.
        _go = null; _img = null; _zimg = null;
        try
        {
            var go = new GameObject("_stellar_monicon");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var comp = go.AddComponent(Il2CppType.From(_zImageType!));   // ZImage component
            if (comp == null) { UnityEngine.Object.Destroy(go); _goFailed = true; return false; }
            _img  = comp.TryCast<Image>();                              // Image base — for .sprite
            _zimg = Activator.CreateInstance(_zImageType!, new object[] { ((Il2CppObjectBase)comp).Pointer });
            _go   = go;
            if (_img == null || _zimg == null) { _goFailed = true; return false; }
            return true;
        }
        catch (Exception ex) { _goFailed = true; _services.Log.Warning($"[MonIcon] go init: {ex.Message}"); return false; }
    }

    public void Dispose()
    {
        try { if (_go != null) UnityEngine.Object.Destroy(_go); } catch { }
        _go = null; _img = null; _zimg = null;
    }

    // ── reflection resolve ─────────────────────────────────────────────────────

    private bool          _reflResolved, _reflOk;
    private Type?         _zImageType;
    private Type?         _zResType;
    private MethodInfo?   _miOpImplicit;   // ZResAddress.op_Implicit(string)
    private MethodInfo?   _miSetImage;     // ZImage.SetImage(ZResAddress)
    private MethodInfo?   _miMonGetTable;  // MonsterTableBase.GetTable(bool)
    private MethodInfo?   _miModelGetTable;// ModelTableBase.GetTable(bool)
    private PropertyInfo? _piModelId;      // MonsterTableBase.ModelID
    private PropertyInfo? _piImage;        // ModelTableBase.Image

    private object?     _monTblObj;        // ZTable<int,MonsterTableBase> (singleton; re-fetched if null)
    private object?     _modelTblObj;      // ZTable<int,ModelTableBase>
    private MethodInfo? _miMonTryGet;      // resolved off the live table type
    private MethodInfo? _miModelTryGet;

    private bool EnsureRefl()
    {
        if (_reflResolved) return _reflOk;
        _reflResolved = true;
        try
        {
            var monTbl   = StellarInterop.FindType("Bokura.MonsterTableBase");
            var modelTbl = StellarInterop.FindType("Bokura.ModelTableBase");
            _zImageType  = StellarInterop.FindType("Panda.ZUi.ZImage");
            _zResType    = StellarInterop.FindType("ZResource.Loader.ZResAddress");
            if (monTbl == null || modelTbl == null || _zImageType == null || _zResType == null)
            {
                _services.Log.Warning($"[MonIcon] type resolve failed mon={monTbl != null} model={modelTbl != null} " +
                    $"zImage={_zImageType != null} zRes={_zResType != null}");
                return false;
            }

            _miMonGetTable   = FindGetTable(monTbl);
            _miModelGetTable = FindGetTable(modelTbl);
            _piModelId       = monTbl.GetProperty("ModelID", BindingFlags.Public | BindingFlags.Instance);
            _piImage         = modelTbl.GetProperty("Image", BindingFlags.Public | BindingFlags.Instance);

            foreach (var m in _zResType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "op_Implicit" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(string))
                { _miOpImplicit = m; break; }

            foreach (var m in _zImageType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "SetImage" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == _zResType)
                { _miSetImage = m; break; }

            _reflOk = _miMonGetTable != null && _miModelGetTable != null && _piModelId != null
                   && _piImage != null && _miOpImplicit != null && _miSetImage != null;
            _services.Log.Info($"[MonIcon] refl ok={_reflOk} monGetTable={_miMonGetTable != null} " +
                $"modelGetTable={_miModelGetTable != null} modelId={_piModelId != null} image={_piImage != null} " +
                $"opImplicit={_miOpImplicit != null} setImage={_miSetImage != null}");
            return _reflOk;
        }
        catch (Exception ex) { _services.Log.Warning($"[MonIcon] refl error: {ex.Message}"); return false; }
    }

    private static MethodInfo? FindGetTable(Type t)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            if (m.Name == "GetTable" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(bool))
                return m;
        return null;
    }

    private static MethodInfo? FindTryGetValue(object table)
    {
        foreach (var m in table.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == "TryGetValue" && m.GetParameters().Length == 3) return m;
        return null;
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    // Il2CppInterop `!= null` misses Unity's fake-null (freed native object, live C# wrapper). Reading an instance
    // member throws on a freed object — a throw or a real null means "dead".
    private static bool Alive(UnityEngine.Object? o)
    {
        if (o is null) return false;
        try { return o.GetInstanceID() != 0; }
        catch { return false; }
    }

    private readonly Dictionary<int, bool> _diag = new();   // ModelID → logged sprite=true yet
    private void Diag(int configId, int modelId, string path, bool got)
    {
        // Log once per distinct ModelID for each phase (start=false, resolved=true).
        if (_diag.TryGetValue(modelId, out var loggedGot) && (loggedGot || !got)) return;
        _diag[modelId] = got;
        _services.Log.Info($"[MonIcon] cfg={configId} model={modelId} path='{path}' sprite={got}");
    }

    private bool _warned;
    private void WarnOnce(string msg) { if (_warned) return; _warned = true; _services.Log.Warning(msg); }
}
