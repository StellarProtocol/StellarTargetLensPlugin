using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.TargetLens;

/// <summary>
/// Boss CAST BAR / chanting ("吟唱") read off the current target. The cast bar is part of the boss HP-frame widget
/// <c>Panda.ZUi.ZUIBossBlood</c> — the server sends only a one-shot cast-begin (+ occasional corrections) and the
/// widget ticks the countdown LOCALLY, so we read the live progress straight off the widget rather than off any
/// entity attribute:
/// <list type="bullet">
///   <item><c>ZUIBossBlood.BossUuid</c> (public long) — matched to <see cref="LastTargetUuid"/> to pick the widget
///     for the CURRENT target.</item>
///   <item><c>isChantingPlay_</c> (bool) — true while casting.</item>
///   <item><c>lab_chanting_name_</c> (<c>ZTMPText</c>) — its <c>text</c> is the on-screen (localized) skill name.</item>
///   <item><c>imgBar_chanting_</c> / <c>imgBar_normal_chanting_</c> / <c>imgBar_danger_chanting_</c>
///     (each a <c>ZImageBar</c>) — the ACTIVE one (whichever has <c>isPlay_ == true</c>) drives the fill. Its
///     <c>totalTime_</c> (float) is the total cast seconds and <c>image_.fillAmount</c> (0..1) the live progress.</item>
/// </list>
///
/// <para>The widget exists ONLY for bona-fide bosses (entities with a boss frame); normal/elite mobs that cast have
/// no <c>ZUIBossBlood</c>, so the overlay simply won't show for them (accepted for v1). No skill id is available →
/// no icon; v1 shows NAME + bar + countdown.</para>
///
/// <para>All members are private IL2CPP fields (Il2CppInterop may surface them as a property OR a field), so they're
/// read with the same property-or-field <c>FindMember</c>/read helpers the shield read uses. Everything is guarded —
/// a miss returns <c>Casting=false</c> and never throws from the poll/render path. Result is frame-cached.</para>
/// </summary>
internal sealed partial class TargetInfoTracker
{
    /// <summary>One frame's cast state off the current boss target.</summary>
    public readonly struct CastInfo
    {
        public readonly bool   Casting;
        public readonly string SkillName;
        public readonly float  TotalSec;
        public readonly float  RemainSec;
        public readonly bool   Danger;    // the danger-variant bar is the active one
        public CastInfo(bool casting, string skillName, float totalSec, float remainSec, bool danger)
        {
            Casting = casting; SkillName = skillName; TotalSec = totalSec; RemainSec = remainSec; Danger = danger;
        }
    }

    // Re-scan the global widget list at most this often (frames) when the cached instance is stale — a target
    // switch scans immediately regardless. FindObjectsOfTypeAll is expensive, so it's never called per-frame.
    private const int CastRescanFrames = 20;

    // ── [CastDiag] opt-in flag + change-gate (mirrors BreakDiag / ThreatDiag) ──
    public  bool   CastDiag;
    private long   _castDiagUuid = long.MinValue;
    private string _castDiagSig  = "";
    // Counters from the LAST scan, surfaced in [CastDiag]: how many ZUIBossBlood instances were seen, and how many
    // of those had BossUuid == target (the game pools multiple frames per boss, most of them hidden/inactive).
    private int    _castFound;
    private int    _castMatched;

    // ── Resolved handles (once) ────────────────────────────────────────────────
    private bool        _castResolved;
    private Type?       _bossBloodType;      // Panda.ZUi.ZUIBossBlood (interop proxy)
    private MemberInfo? _mBossUuid;          // public long BossUuid
    private MemberInfo? _mIsActive;          // isActiveAndEnabled (bool, inherited from Behaviour) — active frame filter
    private MemberInfo? _mIsChanting;        // isChantingPlay_ (bool)
    private MemberInfo? _mChantName;         // lab_chanting_name_ (ZTMPText)
    private MemberInfo? _mBarChanting;       // imgBar_chanting_ (primary)
    private MemberInfo? _mBarNormal;         // imgBar_normal_chanting_
    private MemberInfo? _mBarDanger;         // imgBar_danger_chanting_

    // ZImageBar members (lazy off the runtime bar type; all three bars share it, so resolved once).
    private Type?       _barType;
    private MemberInfo? _mBarTotalTime;      // totalTime_ (float)
    private MemberInfo? _mBarIsPlay;         // isPlay_ (bool)
    private MemberInfo? _mBarImage;          // image_ (ZImage)

    // ZImage.fillAmount / ZTMPText.text (lazy off their runtime types).
    private Type?       _imgType;
    private MemberInfo? _mImgFill;           // fillAmount (float)
    private Type?       _tmpType;
    private MemberInfo? _mTmpText;           // text (string)

    // Cached matched instance + throttled re-scan bookkeeping.
    private object? _bossBloodInstance;      // matched ZUIBossBlood whose BossUuid == LastTargetUuid
    private int     _lastScanFrame = -1;
    private long    _scannedForUuid = long.MinValue;

    // ── Frame-gated result cache ───────────────────────────────────────────────
    private int      _castFrame = -1;
    private CastInfo _castCache;

    /// <summary>
    /// Live cast state off the current boss target (<see cref="LastTargetEntity"/>/<see cref="LastTargetUuid"/>).
    /// Frame-cached: computes once per frame, returns the cache on repeat calls. Returns <c>true</c> only while a
    /// boss frame for the current target is actively chanting; otherwise <c>false</c> with <c>Casting=false</c>.
    /// </summary>
    internal bool TryGetCast(out CastInfo info)
    {
        int f = Time.frameCount;
        if (f == _castFrame) { info = _castCache; return _castCache.Casting; }
        _castFrame = f;
        _castCache = default;

        bool barPlaying = false, danger = false;
        float total = 0f, fill = 0f, remain = 0f;
        string name = "";
        try
        {
            if (EnsureCast())
            {
                var inst = MatchedBossBlood();
                if (inst != null)
                {
                    // isChantingPlay_ is still read for [CastDiag] (in LogCastDiag), but it no longer gates the cast.
                    barPlaying = ReadActiveBar(inst, out total, out fill, out danger);
                    // A chanting ZImageBar genuinely PLAYING (isPlay_ + totalTime_>0) is the real cast signal — the
                    // game fills it from RefreshBossSkillProgress. `isChantingPlay_` was too strict (a matched frame
                    // could show a playing bar while that flag stayed false), so we gate on the bar, not the flag.
                    if (barPlaying && total > 0f)
                    {
                        var lbl = ReadObjMember(_mChantName, inst);
                        if (lbl != null && ResolveTmpText(lbl)) name = ReadStringMember(_mTmpText, lbl);

                        // ── DANGER FILL-DIRECTION FLIP (single spot) ──
                        // Default: every variant fills forward-as-progress → remaining = total*(1-fill).
                        // If in-game [CastDiag] shows the DANGER bar's `remain` GROWING (not shrinking), the danger
                        // variant fills UPWARD — flip ONLY the danger branch by uncommenting the line below.
                        remain = total * (1f - fill);
                        // if (danger) remain = total * fill;
                        if (remain < 0f) remain = 0f;

                        _castCache = new CastInfo(true, name, total, remain, danger);
                    }
                }
            }
        }
        catch { _castCache = default; }

        LogCastDiag(name, remain);
        info = _castCache; return _castCache.Casting;
    }

    // Resolve the ZUIBossBlood type + its cast-bar members once. A missing type/member just degrades the read to
    // "not casting" (logged once), never throws.
    private bool EnsureCast()
    {
        if (_castResolved) return _bossBloodType != null;
        _castResolved = true;
        try
        {
            _bossBloodType = StellarInterop.FindType("Panda.ZUi.ZUIBossBlood");
            if (_bossBloodType == null)
            {
                _services.Log.Warning("[Cast] ZUIBossBlood type not found");
                return false;
            }
            _mBossUuid    = FindMember(_bossBloodType, "BossUuid");
            // Behaviour.isActiveAndEnabled — FindMember uses FlattenHierarchy so the inherited public property
            // resolves; used to prefer the ACTIVE (visible, ticking) frame over pooled/hidden ones.
            _mIsActive    = FindMember(_bossBloodType, "isActiveAndEnabled");
            _mIsChanting  = FindMember(_bossBloodType, "isChantingPlay_");
            _mChantName   = FindMember(_bossBloodType, "lab_chanting_name_");
            _mBarChanting = FindMember(_bossBloodType, "imgBar_chanting_");
            _mBarNormal   = FindMember(_bossBloodType, "imgBar_normal_chanting_");
            _mBarDanger   = FindMember(_bossBloodType, "imgBar_danger_chanting_");
            _services.Log.Info(
                $"[Cast] resolve bossUuid={MemberKind(_mBossUuid)} active={MemberKind(_mIsActive)} " +
                $"chanting={MemberKind(_mIsChanting)} " +
                $"name={MemberKind(_mChantName)} bar={MemberKind(_mBarChanting)} " +
                $"normal={MemberKind(_mBarNormal)} danger={MemberKind(_mBarDanger)}");
            return true;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Cast] resolve error: {ex.Message}");
            return false;
        }
    }

    // Return the ZUIBossBlood widget whose BossUuid == the current target, using a cached instance and only
    // re-scanning the (expensive) global object list when the cache is stale/dead AND the throttle allows it (a
    // target-uuid change forces an immediate scan). Null when the current target has no boss frame.
    private object? MatchedBossBlood()
    {
        long target = LastTargetUuid;
        if (target == 0) { _bossBloodInstance = null; return null; }

        // Validate the cache: still alive, still our target, and still ACTIVE? A pooled/hidden frame never ticks
        // its cast bars, so a cached frame that has gone inactive must be dropped and replaced by a re-scan for
        // the active one. `_lastScanFrame` stays old while a valid cache is served (the scan below is skipped),
        // so this drop lets the throttle re-scan immediately when a long-lived frame finally goes inactive; a
        // freshly-cached last-resort inactive frame keeps `_lastScanFrame` recent, throttling repeat scans →
        // no per-frame FindObjectsOfTypeAll thrash.
        if (_bossBloodInstance != null)
        {
            long u = ReadLongMember(_mBossUuid, _bossBloodInstance);
            if (u == target && IsInstanceActive(_bossBloodInstance)) return _bossBloodInstance;
            _bossBloodInstance = null;   // destroyed / different boss / gone inactive → drop and re-scan
        }

        int f = Time.frameCount;
        // Skip the scan only when we already scanned for THIS target recently and found nothing.
        if (_scannedForUuid == target && f - _lastScanFrame < CastRescanFrames) return null;
        _lastScanFrame  = f;
        _scannedForUuid = target;

        _bossBloodInstance = ScanForBossBlood(target);
        return _bossBloodInstance;
    }

    // Enumerate every live ZUIBossBlood (Resources.FindObjectsOfTypeAll) and return the BEST frame whose BossUuid
    // matches. FindObjectsOfTypeAll returns ALL instances INCLUDING inactive/pooled ones, and the game pools several
    // boss frames per boss (single_/master_/slave_BloodComp) — most hidden and never ticking their cast bars. So we
    // don't return the first match: we collect matches and prefer (a) an ACTIVE frame that is already showing a cast,
    // else (b) any ACTIVE frame, else (c) the first match (old behavior) as a last resort. Each element comes back
    // typed as UnityEngine.Object, so it's re-wrapped as the ZUIBossBlood proxy (all interop proxies expose (IntPtr)).
    private object? ScanForBossBlood(long target)
    {
        _castFound = 0; _castMatched = 0;
        object? firstMatch = null, activeMatch = null, castingMatch = null;
        try
        {
            if (_bossBloodType == null) return null;
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(_bossBloodType));
            if (arr == null) return null;
            int n = arr.Count;
            _castFound = n;
            for (int i = 0; i < n; i++)
            {
                var el = arr[i];
                if (el == null) continue;
                var inst = WrapAs(_bossBloodType, el);
                if (inst == null) continue;
                if (ReadLongMember(_mBossUuid, inst) != target) continue;
                _castMatched++;
                firstMatch ??= inst;
                if (!IsInstanceActive(inst)) continue;      // active-read failure → treated as inactive, degrades to firstMatch
                activeMatch ??= inst;
                if (castingMatch == null && IsShowingCast(inst)) castingMatch = inst;
            }
        }
        catch { /* scan must never throw from the render path */ }
        // active+casting → active → first (last resort preserves the pre-fix behavior when nothing is active).
        return castingMatch ?? activeMatch ?? firstMatch;
    }

    // True when this ZUIBossBlood frame is active in the hierarchy (Behaviour.isActiveAndEnabled). A failed read —
    // missing member or throw — returns false, which degrades the scan to the old first-match behavior. Never throws.
    private bool IsInstanceActive(object? inst)
        => inst != null && ReadBoolMember(_mIsActive, inst);

    // True when this frame currently shows a cast: isChantingPlay_ set, OR any of the three chanting bars is playing.
    // Used only to prefer an already-casting active frame during the scan; guarded, never throws.
    private bool IsShowingCast(object inst)
    {
        try
        {
            if (ReadBoolMember(_mIsChanting, inst)) return true;
            foreach (var m in new[] { _mBarChanting, _mBarNormal, _mBarDanger })
            {
                if (m == null) continue;
                var bar = ReadObjMember(m, inst);
                if (bar == null || !ResolveBarMembers(bar)) continue;
                if (ReadBoolMember(_mBarIsPlay, bar)) return true;
            }
        }
        catch { /* preference probe must never throw */ }
        return false;
    }

    // Pick the ACTIVE cast bar (first of chanting/normal/danger with isPlay_ == true) and read its total + fill.
    // `danger` is true when the danger variant is the active one.
    private bool ReadActiveBar(object inst, out float total, out float fill, out bool danger)
    {
        total = 0f; fill = 0f; danger = false;
        // Priority order: primary chanting bar, then normal, then danger. `dg` flags the danger variant so the
        // overlay can accent it differently (a tiny 3-item array built per call is negligible).
        var list = new (MemberInfo? m, bool dg)[] { (_mBarChanting, false), (_mBarNormal, false), (_mBarDanger, true) };
        foreach (var (m, dg) in list)
        {
            if (m == null) continue;
            var bar = ReadObjMember(m, inst);
            if (bar == null || !ResolveBarMembers(bar)) continue;
            if (!ReadBoolMember(_mBarIsPlay, bar)) continue;   // not the active bar this frame

            total = ReadFloatMember(_mBarTotalTime, bar);
            var img = ReadObjMember(_mBarImage, bar);
            fill  = (img != null && ResolveImgFill(img)) ? ReadFloatMember(_mImgFill, img) : 0f;
            danger = dg;
            return true;
        }
        return false;
    }

    // ── Lazy member resolves off the runtime child types (cached, re-resolve on type change) ──
    private bool ResolveBarMembers(object bar)
    {
        var t = bar.GetType();
        if (t != _barType)
        {
            _barType       = t;
            _mBarTotalTime = FindMember(t, "totalTime_");
            _mBarIsPlay    = FindMember(t, "isPlay_");
            _mBarImage     = FindMember(t, "image_");
        }
        return _mBarTotalTime != null && _mBarIsPlay != null && _mBarImage != null;
    }

    private bool ResolveImgFill(object img)
    {
        var t = img.GetType();
        if (t != _imgType) { _imgType = t; _mImgFill = FindMember(t, "fillAmount"); }
        return _mImgFill != null;
    }

    private bool ResolveTmpText(object lbl)
    {
        var t = lbl.GetType();
        if (t != _tmpType) { _tmpType = t; _mTmpText = FindMember(t, "text"); }
        return _mTmpText != null;
    }

    // ── Member read helpers (property-or-field, all guarded) ──
    private static object? ReadObjMember(MemberInfo? m, object target)
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

    private static bool ReadBoolMember(MemberInfo? m, object target)
        => ReadObjMember(m, target) is bool b && b;

    private static float ReadFloatMember(MemberInfo? m, object target)
    {
        try { var v = ReadObjMember(m, target); return v == null ? 0f : Convert.ToSingle(v); }
        catch { return 0f; }
    }

    private static string ReadStringMember(MemberInfo? m, object target)
        => ReadObjMember(m, target) as string ?? "";

    // Re-wrap an Il2CppInterop UnityEngine.Object element as `proxyType` via its public (IntPtr) constructor.
    private static object? WrapAs(Type proxyType, UnityEngine.Object element)
    {
        try
        {
            IntPtr ptr = element is Il2CppObjectBase b ? IL2CPP.Il2CppObjectBaseToPtr(b) : IntPtr.Zero;
            if (ptr == IntPtr.Zero) return null;
            return Activator.CreateInstance(proxyType, ptr);
        }
        catch { return null; }
    }

    // Change-gated [CastDiag] line (opt-in via the CastDiag flag). Reports the scan census (found / matched), whether
    // the chosen frame is active, the raw isChantingPlay_ flag, and — for EACH of the three chanting bars regardless
    // of which is "active" — its isPlay_ / totalTime_ / fillAmount. Reading all three is what reveals WHICH variant
    // (chanting / normal / danger) the game actually drives during a cast, and its fill direction. The change-gate
    // signature folds in every per-bar value, so a real cast (a bar starting to play) forces a fresh line.
    private void LogCastDiag(string name, float remain)
    {
        if (!CastDiag) return;
        try
        {
            var inst = _bossBloodInstance;
            bool activeMatched = inst != null && IsInstanceActive(inst);
            bool isChant       = inst != null && ReadBoolMember(_mIsChanting, inst);   // raw flag — read, but no longer gates

            ReadBarDiag(inst, _mBarChanting, out bool cP, out float cT, out float cF);
            ReadBarDiag(inst, _mBarNormal,   out bool nP, out float nT, out float nF);
            ReadBarDiag(inst, _mBarDanger,   out bool dP, out float dT, out float dF);

            string sig = $"{_castFound}|{_castMatched}|{activeMatched}|{isChant}|{name}|{remain:F2}|" +
                         $"{cP}{cT:F2}{cF:F3}|{nP}{nT:F2}{nF:F3}|{dP}{dT:F2}{dF:F3}";
            if (LastTargetUuid == _castDiagUuid && sig == _castDiagSig) return;
            _castDiagUuid = LastTargetUuid;
            _castDiagSig  = sig;
            _services.Log.Info(
                $"[CastDiag] uuid={LastTargetUuid} found={_castFound} matched={_castMatched} " +
                $"activeMatched={activeMatched} isChanting={isChant} | " +
                $"c:play={cP} t={cT:F2} f={cF:F3} n:play={nP} t={nT:F2} f={nF:F3} d:play={dP} t={dT:F2} f={dF:F3} | " +
                $"name='{name}' remain={remain:F2}");
        }
        catch { /* diagnostic must never throw */ }
    }

    // Read one chanting bar's isPlay_ / totalTime_ / image_.fillAmount for [CastDiag]. All-guarded; a miss yields zeros.
    private void ReadBarDiag(object? inst, MemberInfo? m, out bool play, out float total, out float fill)
    {
        play = false; total = 0f; fill = 0f;
        try
        {
            if (inst == null || m == null) return;
            var bar = ReadObjMember(m, inst);
            if (bar == null || !ResolveBarMembers(bar)) return;
            play  = ReadBoolMember(_mBarIsPlay, bar);
            total = ReadFloatMember(_mBarTotalTime, bar);
            var img = ReadObjMember(_mBarImage, bar);
            fill  = (img != null && ResolveImgFill(img)) ? ReadFloatMember(_mImgFill, img) : 0f;
        }
        catch { /* diagnostic must never throw */ }
    }
}
