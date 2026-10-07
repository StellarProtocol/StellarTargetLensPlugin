using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Per-class targetable-range resolve for <see cref="NearbyMonsterTracker"/>. When the user enables the
/// "Show only in targetable range" toggle, the Nearby list caps distance by the LOCAL player's actual
/// auto-acquire/lock radius (<c>STRange</c>) instead of the fixed <c>MaxDistanceM</c>. There is NO
/// <c>EAttrType</c> range scalar — the range is per-profession config (metres, same space as the Nearby
/// distance read). Split out of <c>NearbyMonsterTracker.cs</c> to keep that file under the 500-LoC gate.
///
/// Read paths, tried in order, every step fail-soft:
///   A (live, preferred): local player <c>ZEntity</c> → <c>GetComponent&lt;TargetComp&gt;()</c> (the base-walk
///     generic resolve in <c>.Target.cs</c>) → field <c>weaponParams_</c> (<c>Panda.ZGame.ProfessionParams</c>,
///     a struct) → <c>STRange</c>. ⭐ On the RUNTIME struct <c>STRange</c> is a PLAIN <c>float</c> (the game has
///     already collapsed the talent NumberArray to the player's current talent), so NO index-0 walk is needed
///     here — unlike Path B. Il2CppInterop may surface the private <c>weaponParams_</c> as a PROPERTY, so we try
///     <c>GetProperty</c> then <c>GetField</c>.
///   B (fallback, static table): <c>AttrProfessionId</c> (220) off the local player → <c>Bokura.ProfessionTableBase</c>
///     <c>GetTable</c>/<c>TryGetValue</c> keyed by that id → <c>STRange</c>, which HERE is a talent-indexed
///     <c>NumberArray</c> (<c>TableArray&lt;float&gt;</c>) → read element 0. Mirrors the <c>MonsterTableBase</c>
///     reflection in the main file.
///   C (last-ditch): <see cref="DefaultTargetableRangeM"/> so the toggle still does something reasonable.
///
/// Resolved cheaply: profession/talent rarely changes mid-session, so the result is cached on
/// <see cref="TargetableRangeM"/> and only re-read when the local entity changes (scene/char swap) or after
/// <see cref="RangeRefreshMs"/> — never per frame. The reflection runs ONLY while the toggle is on.
/// </summary>
internal sealed partial class NearbyMonsterTracker
{
    // Pushed from Plugin.Settings: ON → cap the Nearby list by TargetableRangeM instead of MaxDistanceM.
    public bool TargetableOnly { get; set; }

    // The resolved per-class targetable range in metres (STRange). DefaultTargetableRangeM until a struct/table
    // read succeeds; then whatever the live/config read produced. Read by Poll (see main file) to set the cap.
    public float TargetableRangeM { get; private set; } = DefaultTargetableRangeM;

    // ⚠️ TUNING CALL (not a bug): STRange ≈ 6 m normal / LargeSTRange ≈ 12 m extended; the user confirms whether
    // this feels right or we widen the column. DefaultTargetableRangeM is only the never-resolved fallback.
    private const float DefaultTargetableRangeM = 15f;   // Path C fallback when neither struct nor table binds
    private const int   AttrProfessionIdId      = 220;   // EAttrType.AttrProfessionId — profession id off the local player
    private const long  RangeRefreshMs          = 30000; // re-read cadence (profession/talent rarely changes → cheap)

    private long _rangeLastLocalUuid;   // 0 = never resolved; a change = scene/char swap → re-resolve
    private long _rangeLastResolveMs;

    // Lazy reflection — its own resolve so the main EnsureApi is untouched. Boxed/null boxes just degrade a path.
    private bool          _rangeApiResolved;
    private object?       _attrProfessionIdBox;   // Enum box (220) for ReadAttr
    private FieldInfo?    _fiStRangeStruct;        // ProfessionParams.STRange (public float) — Path A value
    private PropertyInfo? _piWeaponParams;         // TargetComp.weaponParams_ surfaced as a property (Il2CppInterop)
    private FieldInfo?    _fiWeaponParams;         // TargetComp.weaponParams_ surfaced as the private field
    private MethodInfo?   _miProfGetTable;         // static ProfessionTableBase.GetTable(bool) — Path B
    private PropertyInfo? _piProfStRange;          // ProfessionTableBase.STRange (NumberArray) — Path B value
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

            // Path A — ProfessionParams.STRange (plain float on the runtime struct; see class summary for why there
            // is no talent-index walk here, unlike the Path B table).
            var ppType = StellarInterop.FindType("Panda.ZGame.ProfessionParams");
            if (ppType != null) _fiStRangeStruct = ppType.GetField("STRange", BindingFlags.Public | BindingFlags.Instance);

            // TargetComp.weaponParams_ — a private struct field; Il2CppInterop MAY surface it as a property, so try both.
            var tcType = StellarInterop.FindType("Panda.ZGame.TargetComp");
            if (tcType != null)
            {
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _piWeaponParams = tcType.GetProperty("weaponParams_", any);
                _fiWeaponParams = tcType.GetField("weaponParams_", any);
            }

            // Path B — static ProfessionTableBase table keyed by profession id (STRange there is a talent NumberArray).
            var profTbl = StellarInterop.FindType("Bokura.ProfessionTableBase");
            if (profTbl != null)
            {
                foreach (var m in profTbl.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "GetTable" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(bool))
                    { _miProfGetTable = m; break; }
                _piProfStRange = profTbl.GetProperty("STRange", BindingFlags.Public | BindingFlags.Instance);
            }

            _services.Log.Info($"[Nearby] range api attr={_attrProfessionIdBox != null} ppStRange={_fiStRangeStruct != null} " +
                $"weaponParams(prop={_piWeaponParams != null} field={_fiWeaponParams != null}) " +
                $"profTbl={_miProfGetTable != null} profStRange={_piProfStRange != null}");
        }
        catch (Exception ex) { _services.Log.Warning($"[Nearby] range api resolve error: {ex.Message}"); }
    }

    // Resolve the player's targetable range (STRange), cached. Called from Poll ONLY when TargetableOnly is on, so the
    // reflection never runs for users who leave the toggle off. Re-resolves on local-entity change (scene/char swap)
    // and every RangeRefreshMs; otherwise leaves the cached TargetableRangeM untouched. Logs once per resolve.
    private void EnsureTargetableRange(object? localEnt, long localUuid)
    {
        long now = Environment.TickCount64;
        if (localUuid == _rangeLastLocalUuid && now - _rangeLastResolveMs < RangeRefreshMs) return;
        _rangeLastLocalUuid = localUuid;
        _rangeLastResolveMs = now;
        EnsureRangeApi();

        long profId = localEnt != null ? ReadAttr(localEnt, _attrProfessionIdBox) : 0;

        float r; string src;
        if (TryReadStRangeFromStruct(localEnt, out r))   src = "struct";
        else if (TryReadStRangeFromTable(profId, out r)) src = "table";
        else { r = DefaultTargetableRangeM; src = "default"; }

        TargetableRangeM = r;
        _services.Log.Info($"[Nearby] targetable range={r:0.#} prof={profId} src={src}");
    }

    // Path A: local player ZEntity → TargetComp → weaponParams_ (ProfessionParams) → STRange (float). Returns false on
    // any miss so the caller falls through to the table / default.
    private bool TryReadStRangeFromStruct(object? localEnt, out float range)
    {
        range = 0f;
        if (localEnt == null || _fiStRangeStruct == null || (_fiWeaponParams == null && _piWeaponParams == null)) return false;
        try
        {
            var comp = GetTargetComp(localEnt);   // shares the .Target.cs base-walk GetComponent<TargetComp>() resolve
            if (comp == null) return false;
            object? wp = null;
            try { wp = _piWeaponParams?.GetValue(comp); } catch { wp = null; }
            if (wp == null) { try { wp = _fiWeaponParams?.GetValue(comp); } catch { wp = null; } }
            if (wp == null) return false;
            if (_fiStRangeStruct.GetValue(wp) is float f && f > 0f) { range = f; return true; }
            return false;
        }
        catch { return false; }
    }

    // Path B: static ProfessionTableBase keyed by professionId → STRange NumberArray → element 0. Mirrors the
    // MonsterTableBase GetTable/TryGetValue reflection in the main file; errorWhenNotFound=false so a miss never logs.
    private bool TryReadStRangeFromTable(long profId, out float range)
    {
        range = 0f;
        if (profId <= 0 || _miProfGetTable == null || _piProfStRange == null) return false;
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
            var arr = _piProfStRange.GetValue(row);
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
