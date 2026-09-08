using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Interop machinery for the monster-shield read (see <c>TargetInfoTracker.Shield.cs</c> for the orchestration).
/// Obtains the concrete <c>Google.Protobuf.Collections.RepeatedField&lt;Zproto.ShieldInfo&gt;</c> behind the
/// <c>Zproto.IAttr</c> interface wrapper that <c>ZEntity.GetLuaAttr(60050)</c> hands back, via three techniques
/// tried in order and cached per session:
/// <list type="number">
///   <item><b>Technique 1</b> — typed <c>GetAttr&lt;RepeatedField&lt;ShieldInfo&gt;&gt;(EAttrType,bool)</c>. Reuses
///     the plugin's already-resolved open <c>GetAttr&lt;T&gt;</c> definition, closed over the interop
///     <c>RepeatedField&lt;ShieldInfo&gt;</c> type. Returns the list directly — no interface wrapper involved. This
///     is the preferred path (a reference-type instantiation, so IL2CPP serves it via the shared generic code).</item>
///   <item><b>Technique 2</b> — native <c>get_Value</c> invoke: <c>il2cpp_object_get_class</c> → walk parents with
///     <c>il2cpp_class_get_parent</c> to the class declaring <c>get_Value</c> → <c>il2cpp_runtime_invoke</c> →
///     re-wrap the returned pointer as the closed <c>RepeatedField&lt;ShieldInfo&gt;</c>.</item>
///   <item><b>Technique 3</b> — native field read of <c>value_</c>: walk parents to the class declaring the
///     <c>value_</c> backing field → <c>il2cpp_field_get_value_object</c> → re-wrap.</item>
/// </list>
/// Every technique is fully guarded (throw → null); a failing one never aborts the others.
/// </summary>
internal sealed partial class TargetInfoTracker
{
    // ── Interop handles (resolved once) ─────────────────────────────────────────
    private bool        _shieldInteropResolved;
    private Type?       _shieldInfoInteropType;   // Zproto.ShieldInfo (interop)
    private Type?       _closedRepeatedType;      // RepeatedField<ShieldInfo> (interop, closed) — used to wrap + as T1's T
    private MethodInfo? _miGetAttrShield;         // T1: ZEntity.GetAttr<RepeatedField<ShieldInfo>>(EAttrType,bool)
    private object?     _shieldAttrBox;           // boxed EAttrType value 60050

    // Cached winning technique for the session (0 = not yet proven). Once a technique returns a non-null list we
    // lock to it — later frames run only that one (a null then simply means "no shield up this frame").
    private int    _shieldTech;

    // Resolve the interop types + the typed GetAttr method + the boxed attr value. Idempotent; safe to call every
    // frame. Depends on EnsureApi() having run (it caches _miGetAttrLong + the attr enum boxes) — guaranteed here
    // because a non-null LastTargetEntity implies EnsureApi succeeded during Poll().
    private bool EnsureShieldInterop()
    {
        if (_shieldInteropResolved) return true;
        _shieldInteropResolved = true;
        try
        {
            _shieldInfoInteropType = StellarInterop.FindType("Zproto.ShieldInfo");
            var openRepeated       = StellarInterop.FindType("Google.Protobuf.Collections.RepeatedField`1");
            if (_shieldInfoInteropType != null && openRepeated != null)
            {
                try { _closedRepeatedType = openRepeated.MakeGenericType(_shieldInfoInteropType); }
                catch (Exception ex) { _services.Log.Warning($"[Shield] closed RepeatedField failed: {ex.GetType().Name}"); }
            }

            // Technique 1 method: recover the open GetAttr<T> definition from the cached long instantiation, then
            // close it over the concrete RepeatedField<ShieldInfo> type.
            if (_miGetAttrLong != null && _closedRepeatedType != null)
            {
                try { _miGetAttrShield = _miGetAttrLong.GetGenericMethodDefinition().MakeGenericMethod(_closedRepeatedType); }
                catch (Exception ex) { _services.Log.Warning($"[Shield] GetAttr<Repeated> make failed: {ex.GetType().Name}"); }
            }

            // Boxed EAttrType(60050) — derive the enum type from any already-boxed attr enum value.
            var enumType = _attrHpBox?.GetType();
            if (enumType != null)
                try { _shieldAttrBox = Enum.ToObject(enumType, AttrShieldListId); } catch { }

            _services.Log.Info($"[Shield] interop shieldInfo={_shieldInfoInteropType != null} " +
                               $"closedRepeated={_closedRepeatedType != null} t1Method={_miGetAttrShield != null} " +
                               $"attrBox={_shieldAttrBox != null}");
            return true;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Shield] interop resolve error: {ex.Message}");
            return true; // resolved (with whatever succeeded); techniques self-guard on their missing pieces
        }
    }

    // Acquire the concrete shield list via the cached technique, or (until one is proven) by trying all in order.
    private object? AcquireShieldList(object ent, out int tech)
    {
        tech = 0;
        EnsureShieldInterop();

        if (_shieldTech != 0)
        {
            tech = _shieldTech;
            return RunTechnique(ent, _shieldTech); // null now just means "no shield this frame"
        }
        for (int s = 1; s <= 3; s++)
        {
            object? list = RunTechnique(ent, s);
            if (list != null) { _shieldTech = s; tech = s; return list; }
        }
        return null;
    }

    private object? RunTechnique(object ent, int tech)
    {
        switch (tech)
        {
            case 1:
                return TryTypedGetAttr(ent);
            case 2:
            case 3:
            {
                object? iattr = GetShieldIAttr(ent);
                if (iattr == null) return null;
                return tech == 2 ? TryNativeGetValue(iattr) : TryNativeValueField(iattr);
            }
            default:
                return null;
        }
    }

    // The IAttr interface wrapper via ZEntity.GetLuaAttr(60050) — input to techniques 2 + 3.
    private object? GetShieldIAttr(object ent)
    {
        if (!ResolveGetLuaAttr(ent)) return null;
        try { return _miGetLuaAttr!.Invoke(ent, new object[] { AttrShieldListId }); }
        catch { return null; }
    }

    // ── Technique 1 — typed GetAttr<RepeatedField<ShieldInfo>> (bypasses the interface wrapper) ──
    private object? TryTypedGetAttr(object ent)
    {
        if (_miGetAttrShield == null || _shieldAttrBox == null) return null;
        try { return _miGetAttrShield.Invoke(ent, new object[] { _shieldAttrBox, true }); }
        catch { return null; }
    }

    // ── Technique 2 — native get_Value invoke on the wrapper pointer, then re-wrap the result ──
    private object? TryNativeGetValue(object iattr)
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

            return WrapRepeated(res);
        }
        catch { return null; }
    }

    // ── Technique 3 — native read of the value_ backing field off the wrapper pointer, then re-wrap ──
    private object? TryNativeValueField(object iattr)
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
            return WrapRepeated(obj);
        }
        catch { return null; }
    }

    // Native pointer of an Il2CppInterop-wrapped object (null-safe).
    private static IntPtr NativePtr(object o)
        => o is Il2CppObjectBase b ? IL2CPP.Il2CppObjectBaseToPtr(b) : IntPtr.Zero;

    // Wrap a native RepeatedField<ShieldInfo> object pointer as the closed interop type so its managed Count +
    // indexer become usable. Il2CppInterop reference-type proxies all expose a public (IntPtr) constructor.
    private object? WrapRepeated(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero || _closedRepeatedType == null) return null;
        try { return Activator.CreateInstance(_closedRepeatedType, ptr); }
        catch { return null; }
    }
}
