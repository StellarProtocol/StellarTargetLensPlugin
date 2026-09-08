using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Interop machinery for the threat/aggro read (see <c>TargetInfoTracker.Threat.cs</c> for the orchestration).
/// A near-copy of the shield read (<c>TargetInfoTracker.ShieldRead.cs</c>) adapted to <c>AttrHateList (474)</c> /
/// <c>Zproto.HateInfo</c>. Obtains the concrete <c>Google.Protobuf.Collections.RepeatedField&lt;Zproto.HateInfo&gt;</c>
/// behind the <c>Zproto.IAttr</c> interface wrapper that <c>ZEntity.GetLuaAttr(474)</c> returns, via three
/// techniques tried in order and cached per session:
/// <list type="number">
///   <item><b>Technique 1</b> — typed <c>GetAttr&lt;RepeatedField&lt;HateInfo&gt;&gt;(EAttrType,bool)</c>. Reuses the
///     already-resolved open <c>GetAttr&lt;T&gt;</c> definition, closed over the interop
///     <c>RepeatedField&lt;HateInfo&gt;</c>. Returns the list directly. Expected to win here (the instantiation
///     exists on this build), but confirmed only in-game.</item>
///   <item><b>Technique 2</b> — native <c>get_Value</c> invoke on the wrapper pointer, then re-wrap the result.</item>
///   <item><b>Technique 3</b> — native read of the <c>value_</c> field off the wrapper pointer, then re-wrap.</item>
/// </list>
/// Shares the static helper <c>NativePtr</c> and the <c>GetLuaAttr</c> resolve with the shield read.
/// Every technique is fully guarded (throw → null).
/// </summary>
internal sealed partial class TargetInfoTracker
{
    private const int AttrNameId = 1;             // EAttrType.AttrName — the live entity's display name (string attr)

    // ── Interop handles (resolved once) ─────────────────────────────────────────
    private bool        _threatInteropResolved;
    private Type?       _hateInfoInteropType;      // Zproto.HateInfo (interop)
    private Type?       _closedRepeatedHateType;   // RepeatedField<HateInfo> (interop, closed)
    private MethodInfo? _miGetAttrHate;            // T1: ZEntity.GetAttr<RepeatedField<HateInfo>>(EAttrType,bool)
    private object?     _hateAttrBox;              // boxed EAttrType value 474

    // Player-name read handles (Change 1 — real names off the live entity's AttrName). GetAttr<T> has no string
    // instantiation on this build, but the object instantiation exists, so the primary name read closes GetAttr<T>
    // over object and ToString()s the returned IL2CPP object; the native get_Value fallback marshals the string
    // straight off the GetLuaAttr(1) IAttr wrapper (same wall the shield read hit).
    private MethodInfo? _miGetAttrObject;          // ZEntity.GetAttr<object>(EAttrType,bool) — name read (technique a)
    private object?     _attrNameBox;              // boxed EAttrType value 1 (AttrName)

    // Cached winning technique for the session (0 = not yet proven). Once a technique returns a non-null list we
    // lock to it — later frames run only that one (a null then just means "no hate list this frame").
    private int    _hateTech;

    // Resolve the interop types + the typed GetAttr method + the boxed attr value. Idempotent; safe to call every
    // frame. Depends on EnsureApi() having run (it caches _miGetAttrLong + the attr enum boxes) — guaranteed here
    // because a non-null LastTargetEntity implies EnsureApi succeeded during Poll().
    private bool EnsureThreatInterop()
    {
        if (_threatInteropResolved) return true;
        _threatInteropResolved = true;
        try
        {
            _hateInfoInteropType = StellarInterop.FindType("Zproto.HateInfo");
            var openRepeated     = StellarInterop.FindType("Google.Protobuf.Collections.RepeatedField`1");
            if (_hateInfoInteropType != null && openRepeated != null)
            {
                try { _closedRepeatedHateType = openRepeated.MakeGenericType(_hateInfoInteropType); }
                catch (Exception ex) { _services.Log.Warning($"[Threat] closed RepeatedField failed: {ex.GetType().Name}"); }
            }

            // Technique 1 method: recover the open GetAttr<T> definition from the cached long instantiation, then
            // close it over the concrete RepeatedField<HateInfo> type.
            if (_miGetAttrLong != null && _closedRepeatedHateType != null)
            {
                try { _miGetAttrHate = _miGetAttrLong.GetGenericMethodDefinition().MakeGenericMethod(_closedRepeatedHateType); }
                catch (Exception ex) { _services.Log.Warning($"[Threat] GetAttr<Repeated> make failed: {ex.GetType().Name}"); }
            }

            // Boxed EAttrType(474) — derive the enum type from any already-boxed attr enum value.
            var enumType = _attrHpBox?.GetType();
            if (enumType != null)
            {
                try { _hateAttrBox = Enum.ToObject(enumType, AttrHateListId); } catch { }
                // AttrName(1) box for the primary player-name read (Change 1).
                try { _attrNameBox = Enum.ToObject(enumType, AttrNameId); } catch { }
            }

            // GetAttr<object> — closed off the already-resolved open GetAttr<T>, for the string-name read (technique a).
            if (_miGetAttrLong != null)
                try { _miGetAttrObject = _miGetAttrLong.GetGenericMethodDefinition().MakeGenericMethod(typeof(object)); }
                catch (Exception ex) { _services.Log.Warning($"[Threat] GetAttr<object> make failed: {ex.GetType().Name}"); }

            _services.Log.Info($"[Threat] interop hateInfo={_hateInfoInteropType != null} " +
                               $"closedRepeated={_closedRepeatedHateType != null} t1Method={_miGetAttrHate != null} " +
                               $"attrBox={_hateAttrBox != null} nameAttrBox={_attrNameBox != null} getAttrObj={_miGetAttrObject != null}");
            return true;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Threat] interop resolve error: {ex.Message}");
            return true; // resolved (with whatever succeeded); techniques self-guard on their missing pieces
        }
    }

    // Acquire the concrete hate list via the cached technique, or (until one is proven) by trying all in order.
    private object? AcquireHateList(object ent, out int tech)
    {
        tech = 0;
        EnsureThreatInterop();

        if (_hateTech != 0)
        {
            tech = _hateTech;
            return RunHateTechnique(ent, _hateTech); // null now just means "no hate list this frame"
        }
        for (int s = 1; s <= 3; s++)
        {
            object? list = RunHateTechnique(ent, s);
            if (list != null) { _hateTech = s; tech = s; return list; }
        }
        return null;
    }

    private object? RunHateTechnique(object ent, int tech)
    {
        switch (tech)
        {
            case 1:
                return TryTypedGetAttrHate(ent);
            case 2:
            case 3:
            {
                object? iattr = GetHateIAttr(ent);
                if (iattr == null) return null;
                return tech == 2 ? TryNativeGetValueHate(iattr) : TryNativeValueFieldHate(iattr);
            }
            default:
                return null;
        }
    }

    // The IAttr interface wrapper via ZEntity.GetLuaAttr(474) — input to techniques 2 + 3. Reuses the shield
    // read's GetLuaAttr resolve (shared partial member).
    private object? GetHateIAttr(object ent)
    {
        if (!ResolveGetLuaAttr(ent)) return null;
        try { return _miGetLuaAttr!.Invoke(ent, new object[] { AttrHateListId }); }
        catch { return null; }
    }

    // ── Technique 1 — typed GetAttr<RepeatedField<HateInfo>> (bypasses the interface wrapper) ──
    private object? TryTypedGetAttrHate(object ent)
    {
        if (_miGetAttrHate == null || _hateAttrBox == null) return null;
        try { return _miGetAttrHate.Invoke(ent, new object[] { _hateAttrBox, true }); }
        catch { return null; }
    }

    // ── Technique 2 — native get_Value invoke on the wrapper pointer, then re-wrap the result ──
    private object? TryNativeGetValueHate(object iattr)
    {
        try
        {
            IntPtr ptr = NativePtr(iattr);
            if (ptr == IntPtr.Zero) return null;

            IntPtr method = IntPtr.Zero;
            for (IntPtr k = IL2CPP.il2cpp_object_get_class(ptr); k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
            {
                method = IL2CPP.il2cpp_class_get_method_from_name(k, "get_Value", 0);
                if (method != IntPtr.Zero) break;
            }
            if (method == IntPtr.Zero) return null;

            IntPtr exc = IntPtr.Zero, res;
            unsafe { res = IL2CPP.il2cpp_runtime_invoke(method, ptr, (void**)null, ref exc); }
            if (exc != IntPtr.Zero) return null;

            return WrapRepeatedHate(res);
        }
        catch { return null; }
    }

    // ── Technique 3 — native read of the value_ backing field off the wrapper pointer, then re-wrap ──
    private object? TryNativeValueFieldHate(object iattr)
    {
        try
        {
            IntPtr ptr = NativePtr(iattr);
            if (ptr == IntPtr.Zero) return null;

            IntPtr field = IntPtr.Zero;
            for (IntPtr k = IL2CPP.il2cpp_object_get_class(ptr); k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
            {
                field = IL2CPP.il2cpp_class_get_field_from_name(k, "value_");
                if (field != IntPtr.Zero) break;
            }
            if (field == IntPtr.Zero) return null;

            IntPtr obj = IL2CPP.il2cpp_field_get_value_object(field, ptr);
            return WrapRepeatedHate(obj);
        }
        catch { return null; }
    }

    // Wrap a native RepeatedField<HateInfo> object pointer as the closed interop type so its managed Count +
    // indexer become usable. Il2CppInterop reference-type proxies all expose a public (IntPtr) constructor.
    private object? WrapRepeatedHate(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero || _closedRepeatedHateType == null) return null;
        try { return Activator.CreateInstance(_closedRepeatedHateType, ptr); }
        catch { return null; }
    }

    // ── Player-name reads (Change 1) — the two AttrName techniques the resolution ladder in Threat.cs uses ──

    // Technique (a): typed GetAttr<object>(EAttrType,bool). The attr is a string on the wire; the object
    // instantiation exists on this build, so the returned IL2CPP object ToString()s to the display name. Guarded →
    // "" on any miss (the ladder then tries the native technique, then the non-entity fallbacks).
    private string ReadAttrString(object ent, object? attrBox)
    {
        if (attrBox == null || _miGetAttrObject == null) return "";
        try
        {
            object? r = _miGetAttrObject.Invoke(ent, new object[] { attrBox, true });
            return r?.ToString() ?? "";
        }
        catch { return ""; }
    }

    // Technique (b): native get_Value on the GetLuaAttr(1) IAttr wrapper, marshalling the returned Il2Cpp string
    // object straight to managed — the same wall (and the same native path) the shield read broke through. Used
    // only when technique (a) yields null/empty. Fully guarded → "" on any miss.
    private string ReadAttrNameNative(object ent)
    {
        try
        {
            if (!ResolveGetLuaAttr(ent)) return "";
            object? iattr;
            try { iattr = _miGetLuaAttr!.Invoke(ent, new object[] { AttrNameId }); } catch { return ""; }
            if (iattr == null) return "";

            IntPtr ptr = NativePtr(iattr);
            if (ptr == IntPtr.Zero) return "";

            IntPtr method = IntPtr.Zero;
            for (IntPtr k = IL2CPP.il2cpp_object_get_class(ptr); k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
            {
                method = IL2CPP.il2cpp_class_get_method_from_name(k, "get_Value", 0);
                if (method != IntPtr.Zero) break;
            }
            if (method == IntPtr.Zero) return "";

            IntPtr exc = IntPtr.Zero, res;
            unsafe { res = IL2CPP.il2cpp_runtime_invoke(method, ptr, (void**)null, ref exc); }
            if (exc != IntPtr.Zero || res == IntPtr.Zero) return "";
            return IL2CPP.Il2CppStringToManaged(res) ?? "";
        }
        catch { return ""; }
    }
}
