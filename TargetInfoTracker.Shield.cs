using System;
using System.Reflection;
using UnityEngine;

namespace Stellar.TargetLens;

/// <summary>
/// Monster/boss shield ("armor" / 护盾) read path. The shield is the repeated entity attribute
/// <c>EAttrType.AttrShieldList = 60050</c> — a LIST of <c>Zproto.ShieldInfo</c> (a <b>struct</b>:
/// <c>long Uuid; int ShieldType; long Value; long InitialValue; long MaxValue;</c>). Current shield =
/// Σ <c>Value</c>, max = Σ <c>MaxValue</c>.
///
/// <para>The hard part: <c>ZEntity.GetLuaAttr(int)</c> returns an <b>interface</b> wrapper (<c>Zproto.IAttr</c>) —
/// Il2CppInterop surfaces only <c>ObjectClass/Pointer/WasCollected</c> on it, so managed reflection on
/// <c>.GetType()</c> can't see the concrete <c>Zproto.ZAttr&lt;T&gt;.Value</c>/<c>value_</c>. To reach the concrete
/// <c>RepeatedField&lt;ShieldInfo&gt;</c> behind that wrapper we try three techniques in order (see
/// <c>TargetInfoTracker.ShieldRead.cs</c>) and cache the winner:</para>
/// <list type="number">
///   <item>Typed <c>GetAttr&lt;RepeatedField&lt;ShieldInfo&gt;&gt;</c> — bypasses the interface wrapper entirely.</item>
///   <item>Native <c>get_Value</c> invoke on the wrapper's pointer, then re-wrap the result.</item>
///   <item>Native read of the <c>value_</c> field off the wrapper's pointer, then re-wrap.</item>
/// </list>
/// <para>The obtained list is a concrete used-generic wrapper, so its <c>Count</c>+indexer are visible to managed
/// reflection; each element is a <c>ShieldInfo</c> struct whose <c>Value</c>/<c>MaxValue</c> are read as either a
/// property or a field (Il2CppInterop can surface value-type members either way). Every step is guarded — any miss
/// yields cur=0/max=0 and returns false; it never throws from the poll/render path. Result is frame-cached.</para>
/// </summary>
internal sealed partial class TargetInfoTracker
{
    private const int AttrShieldListId = 60050; // EAttrType.AttrShieldList — repeated ShieldInfo

    // ── GetLuaAttr(int) handle (used by techniques 2 + 3 to obtain the IAttr wrapper) ──
    private bool        _getLuaAttrResolved;
    private MethodInfo? _miGetLuaAttr;    // ZEntity.GetLuaAttr(int) → IAttr

    // ── Concrete list walk (Count + indexer off the runtime list type, cached; re-resolve on type change) ──
    private PropertyInfo? _piListCount;
    private MethodInfo?   _miListGetItem;
    private Type?         _listType;

    // ── ShieldInfo element member read (struct: fields Value@0x10 / MaxValue@0x20; Il2CppInterop may surface them
    //    as either a property or a field, so resolve BOTH and read whichever is present). Cached per element type. ──
    private Type?         _shieldInfoType;
    private MemberInfo?   _shieldValueMember;
    private MemberInfo?   _shieldMaxMember;

    // ── Frame-gated result cache ────────────────────────────────────────────────
    private int  _shieldFrame = -1;
    private long _shieldCur;
    private long _shieldMax;
    private bool _shieldHas;

    /// <summary>
    /// Current summed shield off the live target (<see cref="LastTargetEntity"/>). Frame-cached: computes once
    /// per frame and returns the cache on repeat calls. Returns <c>true</c> with summed cur/max when the shield
    /// list is non-empty and max &gt; 0; otherwise <c>false</c> with cur=max=0.
    /// </summary>
    internal bool TryGetCurrentShield(out long cur, out long max)
    {
        int f = Time.frameCount;
        if (f == _shieldFrame)
        {
            cur = _shieldCur; max = _shieldMax; return _shieldHas;
        }
        _shieldFrame = f;
        _shieldCur = 0; _shieldMax = 0; _shieldHas = false;

        int count = 0;
        try
        {
            var ent = LastTargetEntity;
            if (ent != null)
            {
                object? list = AcquireShieldList(ent, out _);
                if (list != null && ResolveListHandles(list))
                {
                    count = _piListCount!.GetValue(list) is int n ? n : 0;
                    long sumCur = 0, sumMax = 0;
                    for (int i = 0; i < count; i++)
                    {
                        var si = _miListGetItem!.Invoke(list, new object[] { i });
                        if (si == null) continue;
                        if (!ResolveShieldFields(si)) continue;
                        sumCur += ReadLongMember(_shieldValueMember, si);
                        sumMax += ReadLongMember(_shieldMaxMember, si);
                    }
                    _shieldCur = sumCur; _shieldMax = sumMax;
                    _shieldHas = count > 0 && sumMax > 0;
                }
            }
        }
        catch
        {
            _shieldCur = 0; _shieldMax = 0; _shieldHas = false;
        }

        cur = _shieldCur; max = _shieldMax; return _shieldHas;
    }

    // Resolve ZEntity.GetLuaAttr(int) once. Walks the entity's runtime type + base types for the public instance
    // method named GetLuaAttr whose single parameter is System.Int32 (skips the enum-typed overloads).
    private bool ResolveGetLuaAttr(object ent)
    {
        if (_getLuaAttrResolved) return _miGetLuaAttr != null;
        _getLuaAttrResolved = true;
        try
        {
            for (var t = ent.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "GetLuaAttr") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(int)) { _miGetLuaAttr = m; break; }
                }
                if (_miGetLuaAttr != null) break;
            }
        }
        catch { _miGetLuaAttr = null; }
        return _miGetLuaAttr != null;
    }

    // Count (property) + indexer (get_Item) off the runtime list type, cached (re-resolve on type change).
    private bool ResolveListHandles(object list)
    {
        var t = list.GetType();
        if (t != _listType)
        {
            _listType = t;
            _piListCount   = t.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            _miListGetItem = null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "get_Item" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(int))
                { _miListGetItem = m; break; }
        }
        return _piListCount != null && _miListGetItem != null;
    }

    // ShieldInfo.Value / .MaxValue off the runtime element type, cached. ShieldInfo is a STRUCT; Il2CppInterop can
    // surface value-type members as either a property or a field, so resolve whichever exists (property preferred).
    private bool ResolveShieldFields(object si)
    {
        var t = si.GetType();
        if (t != _shieldInfoType)
        {
            _shieldInfoType    = t;
            _shieldValueMember = FindMember(t, "Value");
            _shieldMaxMember   = FindMember(t, "MaxValue");
        }
        return _shieldValueMember != null && _shieldMaxMember != null;
    }

    // Property-then-field lookup across the full type hierarchy (IL2CPP value-type members vary by generator).
    private static MemberInfo? FindMember(Type t, string name)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        try { var p = t.GetProperty(name, F); if (p != null && p.CanRead) return p; } catch { }
        try { var f = t.GetField(name, F); if (f != null) return f; } catch { }
        return null;
    }

    private static long ReadLongMember(MemberInfo? m, object target)
    {
        try
        {
            object? v = m switch
            {
                PropertyInfo p => p.GetValue(target),
                FieldInfo    f => f.GetValue(target),
                _              => null,
            };
            return v == null ? 0L : Convert.ToInt64(v);
        }
        catch { return 0L; }
    }
}
