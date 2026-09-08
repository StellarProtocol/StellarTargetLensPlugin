using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Harmony capture of the game's general cast/channel ("吟唱/读条") begin/end, feeding
/// <see cref="TargetInfoTracker"/>'s cast-bar read. This REPLACES the old approach of scraping the boss HP-frame
/// widget <c>Panda.ZUi.ZUIBossBlood</c>, which only exists for bona-fide bosses — so normal/elite mobs that channel
/// showed no bar, and the read had to guess the fill direction off an image's <c>fillAmount</c>.
///
/// <para>Instead we patch the PRODUCER on every non-local combat entity's state machine:
/// <c>Panda.ZGame.ZStateSkillComp.beginSingGuide()</c> and <c>endSingGuide()</c> (both take NO parameters → no
/// in/ref-struct → no trampoline-NullRef risk; real non-inlined bodies). The begin postfix reads the cast payload
/// straight off <c>__instance</c> (<c>curSkillId_</c>, <c>skillRow_.Name</c>, <c>curSkillSpeedRate_</c>,
/// total via <c>SingGuideSystem.GetTotalSingDuration</c>) and the caster uuid by base-walking
/// <c>ZStateSkillComp : ZStateComponent : ZComponent</c> to <c>ZComponent.Host</c>→<c>ZEntity.Uuid</c> — the same
/// Host→Uuid walk <see cref="BuffTrackPatch"/> uses. It upserts a latch keyed by caster uuid; the end postfix removes
/// that caster's entry, so an interrupt clears the bar PROMPTLY (no widget-tick to drain).</para>
///
/// <para>Threading: both postfixes run on the game/main thread, and the tracker reads the latch on that same thread
/// during window render (one poll per frame) — mirroring <see cref="BuffTrackPatch"/>/<see cref="DbmPatch"/>, so a
/// plain dictionary swap is safe, no lock. A version counter lets the tracker tell a fresh event from a repeat read.
/// The postfixes MUST NEVER THROW (a throw in a game-thread postfix is dangerous) → everything is guarded.</para>
/// </summary>
internal static class CastPatch
{
    /// <summary>One caster's in-flight cast, latched at begin and dropped at end.</summary>
    public struct CastEntry
    {
        public int    SkillId;
        public string Name;
        public float  TotalSec;   // wall-clock cast seconds (game total ÷ speed-rate)
        public long   SnapTick;   // Environment.TickCount64 at begin — the count-up anchor
        public bool   Danger;     // MonsterDanger cast → red accent (default false; see note in OnBeginSing)
    }

    // Latch keyed by caster uuid. Written by the begin/end postfixes, read by the tracker — all on the main thread,
    // so no lock (matches BuffTrackPatch._activeBuffs / DbmPatch._ids).
    private static readonly Dictionary<long, CastEntry> _casts = new();
    private static int _version;   // bumped on every begin/end so the tracker can gate a fresh event

    /// <summary>True once both postfixes are installed (surfaced in [CastDiag] so we can confirm they fired).</summary>
    public static bool Installed { get; private set; }

    /// <summary>Opt-in begin/end event logging (wired from the plugin's "Cast-bar diagnostic" toggle).</summary>
    public static bool Diag;

    /// <summary>Number of casters currently mid-cast (for the diagnostic census).</summary>
    public static int ActiveCount => _casts.Count;

    /// <summary>Latch lookup for the current target. Returns the in-flight cast for <paramref name="casterUuid"/>.</summary>
    public static bool TryGet(long casterUuid, out CastEntry entry) => _casts.TryGetValue(casterUuid, out entry);

    private static Action<string>? _log;

    internal static bool Install(Harmony harmony, Action<string> log)
    {
        _log = log;

        var t = StellarInterop.FindType("Panda.ZGame.ZStateSkillComp");
        if (t == null) { log("[Cast] ZStateSkillComp not found — cast capture skipped"); return false; }

        var begin = ResolveMethod0(t, "beginSingGuide");
        var end   = ResolveMethod0(t, "endSingGuide");
        if (begin == null || end == null)
        {
            log($"[Cast] hooks missing begin={begin != null} end={end != null} — cast capture skipped");
            return false;
        }

        try
        {
            harmony.Patch(begin, postfix: new HarmonyMethod(typeof(CastPatch), nameof(OnBeginSing)));
            harmony.Patch(end,   postfix: new HarmonyMethod(typeof(CastPatch), nameof(OnEndSing)));
            Installed = true;
            log("[Cast] beginSingGuide/endSingGuide postfixes patched");
            return true;
        }
        catch (Exception ex) { log($"[Cast] patch failed: {ex.Message}"); return false; }
    }

    // Harmony teardown is owned by IHarmonyHost (auto-unpatch on dispose); reset only transient state here.
    internal static void Uninstall()
    {
        _casts.Clear();
        _version = 0;
        _reflResolved = false;
        _mCurSkillId = _mSpeedRate = _mSkillRow = _mSkillName = null;
        _skillRowType = null;
        _piHost = _piUuid = null; _hostResolved = false;
        _singSysResolved = false; _singSysType = null; _miGetTotal = null;
        _loggedError = false;
    }

    // ── Begin postfix ────────────────────────────────────────────────────────────────────────────────────────
    // curSkillId_ / skillRow_.Name / curSkillSpeedRate_ off __instance; total via SingGuideSystem.GetTotalSingDuration
    // (÷ speed for the real wall-clock duration under haste); caster uuid via the Host→Uuid base-walk. Fully guarded.
    private static void OnBeginSing(object __instance)
    {
        try
        {
            if (__instance == null) return;
            EnsureRefl(__instance.GetType());

            long uuid = GetHostUuid(__instance);
            if (uuid == 0) return;   // no caster identity → can't key the latch; nothing we can show

            int    skillId = ReadInt(_mCurSkillId, __instance);
            float  speed   = ReadFloat(_mSpeedRate, __instance);
            string name    = ReadSkillName(__instance);

            float total = ResolveTotal(skillId, __instance);
            // curSkillSpeedRate_ is the cast-speed factor (haste): the game total is at 1.0x, so the real wall-clock
            // duration is total ÷ speed. Guard a zero/negative rate (treat as 1.0x) to avoid a divide blow-up.
            if (speed > 0f) total /= speed;

            // ESingGuideType (Normal/MonsterNormal/MonsterDanger/SteelBar) is NOT a field on ZStateSkillComp — it
            // lives on the ECS SingGuideComponent, which is awkward to reach from here. Per the plan we DON'T block
            // the feature on it: default to a non-danger forward (count-up) bar and log begin/end so we can confirm
            // the hook fires for non-boss casters. If danger styling is wanted later, patch EntityExtensions
            // .SetSingGuide(entity, value, maxValue, forward, type) instead — it carries the type explicitly.
            bool danger = false;

            _casts[uuid] = new CastEntry
            {
                SkillId  = skillId,
                Name     = name,
                TotalSec = total,
                SnapTick = Environment.TickCount64,
                Danger   = danger,
            };
            _version++;

            if (Diag)
                _log?.Invoke($"[CastDiag] begin uuid={uuid} skillId={skillId} name='{name}' total={total:F2} " +
                             $"speed={speed:F2} danger={danger} active={_casts.Count}");
        }
        catch (Exception ex) { LogError("begin", ex); }
    }

    // ── End postfix ──────────────────────────────────────────────────────────────────────────────────────────
    // Remove this caster from the latch → the bar hides the moment a cast finishes OR is interrupted. Guarded.
    private static void OnEndSing(object __instance)
    {
        try
        {
            if (__instance == null) return;
            long uuid = GetHostUuid(__instance);
            if (uuid == 0) return;
            if (_casts.Remove(uuid)) _version++;
            if (Diag) _log?.Invoke($"[CastDiag] end uuid={uuid} active={_casts.Count}");
        }
        catch (Exception ex) { LogError("end", ex); }
    }

    // ── Reflection (resolved once off the first ZStateSkillComp instance type) ───────────────────────────────
    private static bool        _reflResolved;
    private static MemberInfo? _mCurSkillId;   // curSkillId_ (int)
    private static MemberInfo? _mSpeedRate;    // curSkillSpeedRate_ (float)
    private static MemberInfo? _mSkillRow;     // skillRow_ (SkillTableBase)
    private static Type?       _skillRowType;  // last-seen runtime type of skillRow_ (lazy re-resolve on change)
    private static MemberInfo? _mSkillName;    // SkillTableBase.Name (string)

    private static void EnsureRefl(Type t)
    {
        if (_reflResolved) return;
        _reflResolved = true;
        _mCurSkillId = FindMember(t, "curSkillId_");
        _mSpeedRate  = FindMember(t, "curSkillSpeedRate_");
        _mSkillRow   = FindMember(t, "skillRow_");
        _log?.Invoke($"[Cast] refl skillId={_mCurSkillId != null} speed={_mSpeedRate != null} row={_mSkillRow != null}");
    }

    private static string ReadSkillName(object inst)
    {
        var row = ReadMember(_mSkillRow, inst);
        if (row == null) return "";
        var rt = row.GetType();
        if (rt != _skillRowType) { _skillRowType = rt; _mSkillName = FindMember(rt, "Name"); }
        return ReadMember(_mSkillName, row) as string ?? "";
    }

    // ── Caster uuid: base-walk ZStateSkillComp → ZComponent.Host (internal ZEntity) → ZEntity.Uuid (long).
    //    Mirrors BuffTrackPatch.ResolveHostRefl/GetHostEntityUuid. ─────────────────────────────────────────────
    private static PropertyInfo? _piHost;
    private static PropertyInfo? _piUuid;
    private static bool          _hostResolved;

    private static long GetHostUuid(object comp)
    {
        if (!_hostResolved)
        {
            _hostResolved = true;
            var cur = comp.GetType();
            while (cur != null && _piHost == null)
            {
                _piHost = cur.GetProperty("Host", BindingFlags.NonPublic | BindingFlags.Instance)
                       ?? cur.GetProperty("Host", BindingFlags.Public | BindingFlags.Instance);
                cur = cur.BaseType;
            }
            if (_piHost != null)
                _piUuid = _piHost.PropertyType.GetProperty("Uuid", BindingFlags.Public | BindingFlags.Instance);
            _log?.Invoke($"[Cast] hostRefl host={_piHost != null} uuid={_piUuid != null}");
        }
        if (_piHost == null || _piUuid == null) return 0L;
        try
        {
            var host = _piHost.GetValue(comp);
            return host == null ? 0L : Convert.ToInt64(_piUuid.GetValue(host) ?? 0L);
        }
        catch { return 0L; }
    }

    // ── Total cast seconds: SingGuideSystem.GetTotalSingDuration(skillId) (static float), else skillRow_
    //    .SingOrGuideTime[1][1] (NumberTable → total sing seconds). Guarded → 0 on total miss (bar shows name only).
    private static bool        _singSysResolved;
    private static Type?       _singSysType;
    private static MethodInfo? _miGetTotal;

    private static float ResolveTotal(int skillId, object inst)
    {
        if (!_singSysResolved)
        {
            _singSysResolved = true;
            _singSysType = StellarInterop.FindType("Panda.ZGame.SingGuideSystem");
            if (_singSysType != null)
                _miGetTotal = _singSysType.GetMethod("GetTotalSingDuration",
                    BindingFlags.Public | BindingFlags.Static);
            _log?.Invoke($"[Cast] singSys type={_singSysType != null} getTotal={_miGetTotal != null}");
        }
        if (_miGetTotal != null)
        {
            try
            {
                var v = _miGetTotal.Invoke(null, new object[] { skillId });
                float f = v == null ? 0f : Convert.ToSingle(v);
                if (f > 0f) return f;
            }
            catch { }
        }
        // Fallback: skillRow_.SingOrGuideTime as a NumberTable, row [1] column [1] = total sing seconds.
        try
        {
            var row = ReadMember(_mSkillRow, inst);
            if (row != null)
            {
                var sog = row.GetType().GetProperty("SingOrGuideTime", BindingFlags.Public | BindingFlags.Instance)?.GetValue(row);
                float f = ReadNumberTable(sog, 1, 1);
                if (f > 0f) return f;
            }
        }
        catch { }
        return 0f;
    }

    // NumberTable : TwoDArray<NumberArray,float> — get_Item(row) → NumberArray, get_Item(col) → float. All guarded.
    private static float ReadNumberTable(object? table, int row, int col)
    {
        try
        {
            if (table == null) return 0f;
            var rowObj = table.GetType().GetMethod("get_Item", new[] { typeof(int) })?.Invoke(table, new object[] { row });
            if (rowObj == null) return 0f;
            var val = rowObj.GetType().GetMethod("get_Item", new[] { typeof(int) })?.Invoke(rowObj, new object[] { col });
            return val == null ? 0f : Convert.ToSingle(val);
        }
        catch { return 0f; }
    }

    // ── Small helpers ────────────────────────────────────────────────────────────────────────────────────────

    // Locate a 0-arg method by name. Prefers StellarInterop.FindMethod (count-only match), falling back to a direct
    // NonPublic scan since beginSingGuide/endSingGuide are private (belt-and-suspenders if FindMethod is public-only).
    private static MethodInfo? ResolveMethod0(Type t, string name)
    {
        var m = StellarInterop.FindMethod(t, name, 0);
        if (m != null) return m;
        try
        {
            foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (mi.Name == name && mi.GetParameters().Length == 0) return mi;
        }
        catch { }
        return null;
    }

    // Property-then-field lookup across the full hierarchy (IL2CPP members surface as either, by generator).
    private static MemberInfo? FindMember(Type t, string name)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        try { var p = t.GetProperty(name, F); if (p != null && p.CanRead) return p; } catch { }
        try { var f = t.GetField(name, F); if (f != null) return f; } catch { }
        return null;
    }

    private static object? ReadMember(MemberInfo? m, object target)
    {
        try
        {
            return m switch
            {
                PropertyInfo p => p.GetValue(target),
                FieldInfo    f => f.GetValue(target),
                _              => null,
            };
        }
        catch { return null; }
    }

    private static int ReadInt(MemberInfo? m, object t)
    {
        var v = ReadMember(m, t);
        try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
    }

    private static float ReadFloat(MemberInfo? m, object t)
    {
        var v = ReadMember(m, t);
        try { return v == null ? 0f : Convert.ToSingle(v); } catch { return 0f; }
    }

    private static bool _loggedError;

    private static void LogError(string src, Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        _log?.Invoke($"[Cast] {src}: {ex.InnerException?.Message ?? ex.Message}");
    }
}
