using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Per-class LOCKABLE-range resolve for <see cref="NearbyMonsterTracker"/>. When the user enables the
/// "Only in lockable range" toggle, the Nearby list caps distance by the LOCAL player's <c>LockCancelRange</c> instead
/// of the fixed <c>MaxDistanceM</c>. There is NO <c>EAttrType</c> range scalar — the range is per-profession config
/// (metres, same centre-to-centre space as the Nearby distance read). Split out of <c>NearbyMonsterTracker.cs</c> to
/// keep that file under the 500-LoC gate.
///
/// ⭐ Why LockCancelRange and not STRange (KB Target-Tracking.md "Target / lock RANGES", 2026-11-12): <c>STRange</c>
/// (6 m melee / 15 m ranged) is only the AUTO-search radius. Lock-on itself has NO acquire range
/// (<c>LockTargetOrCancel()</c> locks the current selection at any distance); the game's per-tick
/// <c>checkTargetCanceled</c> then DROPS a lock past <c>LockCancelRange</c> (8 m melee / 18 m ranged). So "what you can
/// lock and keep" = LockCancelRange — a row past it would lock and immediately fall off.
///
/// Read paths, tried in order, every step fail-soft:
///   A (live, preferred): local player <c>ZEntity</c> → <c>GetComponent&lt;TargetComp&gt;()</c> (the base-walk
///     generic resolve in <c>.Target.cs</c>) → field <c>weaponParams_</c> (<c>Panda.ZGame.ProfessionParams</c>,
///     a blittable struct) → <c>LockCancelRange</c>. ⭐ On the RUNTIME struct it is a PLAIN <c>float</c> (the game has
///     already collapsed the talent NumberArray to the player's current talent), so NO index-0 walk is needed
///     here — unlike Path B. Il2CppInterop may surface the private <c>weaponParams_</c> as a PROPERTY, so we try
///     <c>GetProperty</c> then <c>GetField</c>.
///   B (fallback, static table): <c>AttrProfessionId</c> (220) off the local player → <c>Bokura.ProfessionTableBase</c>
///     <c>GetTable</c>/<c>TryGetValue</c> keyed by that id → <c>LockCancelRange</c>, which HERE is a talent-indexed
///     <c>NumberArray</c> (<c>TableArray&lt;float&gt;</c>) → read element 0. Mirrors the <c>MonsterTableBase</c>
///     reflection in the main file.
///   C (last-ditch): <see cref="DefaultLockableRangeM"/> (the ranged value) so the toggle still does something sane.
///
/// Resolved cheaply: profession/talent rarely changes mid-session, so the result is cached on
/// <see cref="LockableRangeM"/> and only re-read when the local entity changes (scene/char swap) or after
/// <see cref="RangeRefreshMs"/> — never per frame. The reflection runs ONLY while the toggle is on.
/// </summary>
internal sealed partial class NearbyMonsterTracker
{
    // Pushed from Plugin.Settings: ON → cap the Nearby list by LockableRangeM instead of MaxDistanceM.
    public bool LockableRangeOnly { get; set; }

    // The resolved per-class lockable range in metres (LockCancelRange). DefaultLockableRangeM until a struct/table read
    // succeeds; then whatever the live/config read produced. Read by Poll (see main file) to set the cap.
    public float LockableRangeM { get; private set; } = DefaultLockableRangeM;

    // True once Path A or B produced a real value (false = still the Path C default). The Settings label shows
    // "(N m)" only when this is set, so it never advertises the guessed fallback as the player's actual range.
    public bool LockableRangeResolved { get; private set; }

    private const float DefaultLockableRangeM = 18f;   // Path C fallback (ranged LockCancelRange) when neither path binds
    private const int   AttrProfessionIdId    = 220;   // EAttrType.AttrProfessionId — profession id off the local player
    private const long  RangeRefreshMs        = 30000; // re-read cadence (profession/talent rarely changes → cheap)

    private long _rangeLastLocalUuid;   // 0 = never resolved; a change = scene/char swap → re-resolve
    private long _rangeLastResolveMs;

    // Lazy reflection — its own resolve so the main EnsureApi is untouched. Boxed/null boxes just degrade a path.
    private bool          _rangeApiResolved;
    private object?       _attrProfessionIdBox;   // Enum box (220) for ReadAttr
    private FieldInfo?    _fiLockRangeStruct;      // ProfessionParams.LockCancelRange (public float) — Path A value
    private PropertyInfo? _piWeaponParams;         // TargetComp.weaponParams_ surfaced as a property (Il2CppInterop)
    private FieldInfo?    _fiWeaponParams;         // TargetComp.weaponParams_ surfaced as the private field
    private MethodInfo?   _miProfGetTable;         // static ProfessionTableBase.GetTable(bool) — Path B
    private PropertyInfo? _piProfLockRange;        // ProfessionTableBase.LockCancelRange (NumberArray) — Path B value
    private object?       _profTblObj;
    private MethodInfo?   _miProfTryGetValue;
    private bool          _profTryGetResolved;

    private void EnsureRangeApi()
    {
        if (_rangeApiResolved) return;
        _rangeApiResolved = true;
        try
        {
            var attrEnum = StellarInterop.FindType("Zproto.EAttrType");
            if (attrEnum != null) _attrProfessionIdBox = Enum.ToObject(attrEnum, AttrProfessionIdId);

            // Path A — ProfessionParams.LockCancelRange (plain float on the runtime struct, dump offset 0x24; see the
            // class summary for why there is no talent-index walk here, unlike the Path B table).
            var ppType = StellarInterop.FindType("Panda.ZGame.ProfessionParams");
            if (ppType != null) _fiLockRangeStruct = ppType.GetField("LockCancelRange", BindingFlags.Public | BindingFlags.Instance);

            // TargetComp.weaponParams_ — a private struct field; Il2CppInterop MAY surface it as a property, so try both.
            var tcType = StellarInterop.FindType("Panda.ZGame.TargetComp");
            if (tcType != null)
            {
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _piWeaponParams = tcType.GetProperty("weaponParams_", any);
                _fiWeaponParams = tcType.GetField("weaponParams_", any);
            }

            // Path B — static ProfessionTableBase keyed by profession id (LockCancelRange there is a talent NumberArray).
            var profTbl = StellarInterop.FindType("Bokura.ProfessionTableBase");
            if (profTbl != null)
            {
                foreach (var m in profTbl.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "GetTable" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(bool))
                    { _miProfGetTable = m; break; }
                _piProfLockRange = profTbl.GetProperty("LockCancelRange", BindingFlags.Public | BindingFlags.Instance);
            }
        }
        catch (Exception ex) { _services.Log.Warning($"[Nearby] range api resolve error: {ex.Message}"); }
    }

    // Resolve the player's lockable range (LockCancelRange), cached. Called from Poll ONLY when LockableRangeOnly is on,
    // so the reflection never runs for users who leave the toggle off. Re-resolves on local-entity change (scene/char
    // swap) and every RangeRefreshMs; otherwise leaves the cached LockableRangeM untouched.
    private void EnsureLockableRange(object? localEnt, long localUuid)
    {
        long now = Environment.TickCount64;
        if (localUuid == _rangeLastLocalUuid && now - _rangeLastResolveMs < RangeRefreshMs) return;
        _rangeLastLocalUuid = localUuid;
        _rangeLastResolveMs = now;
        EnsureRangeApi();

        long profId = localEnt != null ? ReadAttr(localEnt, _attrProfessionIdBox) : 0;

        bool ok = TryReadLockRangeFromStruct(localEnt, out float r) || TryReadLockRangeFromTable(profId, out r);
        LockableRangeM = ok ? r : DefaultLockableRangeM;
        LockableRangeResolved = ok;
    }

    // Path A: local player ZEntity → TargetComp → weaponParams_ (ProfessionParams) → LockCancelRange (float). Returns
    // false on any miss so the caller falls through to the table / default.
    private bool TryReadLockRangeFromStruct(object? localEnt, out float range)
    {
        range = 0f;
        if (localEnt == null || _fiLockRangeStruct == null || (_fiWeaponParams == null && _piWeaponParams == null)) return false;
        try
        {
            var comp = GetTargetComp(localEnt);   // shares the .Target.cs base-walk GetComponent<TargetComp>() resolve
            if (comp == null) return false;
            object? wp = null;
            try { wp = _piWeaponParams?.GetValue(comp); } catch { wp = null; }
            if (wp == null) { try { wp = _fiWeaponParams?.GetValue(comp); } catch { wp = null; } }
            if (wp == null) return false;
            if (_fiLockRangeStruct.GetValue(wp) is float f && f > 0f) { range = f; return true; }
            return false;
        }
        catch { return false; }
    }

    // Path B: static ProfessionTableBase keyed by professionId → LockCancelRange NumberArray → element 0. Mirrors the
    // MonsterTableBase GetTable/TryGetValue reflection in the main file; errorWhenNotFound=false so a miss never logs.
    private bool TryReadLockRangeFromTable(long profId, out float range)
    {
        range = 0f;
        if (profId <= 0 || _miProfGetTable == null || _piProfLockRange == null) return false;
        try
        {
            _profTblObj ??= _miProfGetTable.Invoke(null, new object[] { false });
            if (_profTblObj == null) return false;
            if (!_profTryGetResolved)
            {
                foreach (var m in _profTblObj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "TryGetValue" && m.GetParameters().Length == 3) { _miProfTryGetValue = m; break; }
                _profTryGetResolved = true;
            }
            if (_miProfTryGetValue == null) return false;
            var args = new object?[] { (int)profId, null, false };
            bool ok = _miProfTryGetValue.Invoke(_profTblObj, args) is bool b && b;
            var row = ok ? args[1] : null;
            if (row == null) return false;
            var arr = _piProfLockRange.GetValue(row);
            if (arr == null) return false;
            return TryReadNumberArray0(arr, out range);
        }
        catch { return false; }
    }

    // Read element 0 of a NumberArray (TableArray<float>): prefer the explicit GetValue(int), fall back to the
    // get_Item(int) indexer, guarded by Length. All reflection (the array type isn't referenced at compile time).
    private static bool TryReadNumberArray0(object arr, out float v)
    {
        v = 0f;
        try
        {
            var t = arr.GetType();
            // Length==0 → genuinely empty → no value. Length unreadable (-1) → still attempt the element read.
            int len = t.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance)?.GetValue(arr) is int n ? n : -1;
            if (len == 0) return false;
            var get = t.GetMethod("GetValue", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null)
                      ?? t.GetMethod("get_Item", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            if (get == null) return false;
            if (get.Invoke(arr, new object[] { 0 }) is float f && f > 0f) { v = f; return true; }
            return false;
        }
        catch { return false; }
    }
}
