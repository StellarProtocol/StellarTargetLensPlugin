using System;
using UnityEngine;

namespace Stellar.TargetLens;

/// <summary>
/// CAST BAR / channel ("吟唱/读条") read for the CURRENT target — now HOOK-SOURCED via <see cref="CastPatch"/> rather
/// than scraped off the boss HP-frame widget. The patch latches every non-local caster's in-flight cast (keyed by
/// caster uuid) at <c>beginSingGuide()</c> and drops it at <c>endSingGuide()</c>; this read simply looks up the entry
/// for <see cref="LastTargetUuid"/>. Consequences vs the old widget scrape:
/// <list type="bullet">
///   <item>Works for ANY caster — boss, elite, or normal mob — not just entities that own a <c>ZUIBossBlood</c> frame.</item>
///   <item>We have the skill id, so the overlay can show the skill ICON, not just the name.</item>
///   <item>Counts UP (elapsed 0→Total, matching the in-game bar) instead of a scraped remaining fraction.</item>
///   <item>Clears PROMPTLY on interrupt — the end hook removes the latch entry; no widget countdown to drain.</item>
/// </list>
///
/// <para>Frame-cached (one read per frame). A safety-expire drops an entry when elapsed runs past the total (in case
/// an <c>endSingGuide</c> is ever missed), but the normal hide path is the end hook. Guarded — never throws.</para>
/// </summary>
internal sealed partial class TargetInfoTracker
{
    /// <summary>One frame's cast state off the current target. Elapsed COUNTS UP (0→Total), matching the game bar.</summary>
    public readonly struct CastInfo
    {
        public readonly bool   Casting;
        public readonly int    SkillId;      // leveled cast id — drives the overlay icon
        public readonly string SkillName;
        public readonly float  TotalSec;     // wall-clock cast seconds (already speed-adjusted by CastPatch)
        public readonly float  ElapsedSec;   // rises 0 → TotalSec
        public readonly bool   Danger;       // MonsterDanger cast → red accent
        public CastInfo(bool casting, int skillId, string skillName, float totalSec, float elapsedSec, bool danger)
        {
            Casting = casting; SkillId = skillId; SkillName = skillName;
            TotalSec = totalSec; ElapsedSec = elapsedSec; Danger = danger;
        }
    }

    // Safety-expire: a cast with a known total that overruns by this grace is treated as ended (a missed end hook);
    // an unknown-total cast (couldn't resolve seconds) is capped absolutely so it can never linger forever.
    private const float CastGraceSec   = 0.75f;
    private const float CastHardCapSec = 60f;

    // ── [CastDiag] opt-in flag + change-gate (mirrors BreakDiag / ThreatDiag) ──
    public  bool   CastDiag;
    private string _castDiagSig = "";

    // ── Frame-gated result cache ───────────────────────────────────────────────
    private int      _castFrame = -1;
    private CastInfo _castCache;

    /// <summary>
    /// Live cast state off the current target (<see cref="LastTargetUuid"/>). Frame-cached: computes once per frame,
    /// returns the cache on repeat calls. Returns <c>true</c> only while the current target is mid-cast; otherwise
    /// <c>false</c> with <c>Casting=false</c>. Works for any target type (boss / elite / normal mob).
    /// </summary>
    internal bool TryGetCast(out CastInfo info)
    {
        int f = Time.frameCount;
        if (f == _castFrame) { info = _castCache; return _castCache.Casting; }
        _castFrame = f;
        _castCache = default;

        try
        {
            long uuid = LastTargetUuid;
            if (uuid != 0 && CastPatch.TryGet(uuid, out var e))
            {
                float elapsed = (Environment.TickCount64 - e.SnapTick) / 1000f;
                if (elapsed < 0f) elapsed = 0f;

                // Prompt hide comes from endSingGuide (CastPatch drops the entry). This is only the safety net for a
                // missed end: past total+grace (known total), or the absolute hard cap (unknown total).
                bool overrun = (e.TotalSec > 0f && elapsed >= e.TotalSec + CastGraceSec) || elapsed >= CastHardCapSec;
                if (!overrun)
                {
                    // COUNT UP: expose elapsed, clamped to Total so the bar fills exactly to full and holds there.
                    float shown = e.TotalSec > 0f ? MathF.Min(e.TotalSec, elapsed) : elapsed;
                    _castCache = new CastInfo(true, e.SkillId, e.Name ?? "", e.TotalSec, shown, e.Danger);
                }
            }
        }
        catch { _castCache = default; }

        LogCastDiag();
        info = _castCache; return _castCache.Casting;
    }

    // Change-gated [CastDiag] line (opt-in via CastDiag). Confirms the read tracks the hook: which target, whether it
    // is casting, the skill id/name, the count-up elapsed/total, danger, and how many casters are live in the latch
    // (so a non-boss cast that fires the hook is visible even if it isn't the current target). Never throws.
    private void LogCastDiag()
    {
        if (!CastDiag) return;
        try
        {
            var c = _castCache;
            string sig = $"{LastTargetUuid}|{c.Casting}|{c.SkillId}|{c.SkillName}|{c.ElapsedSec:F1}/{c.TotalSec:F1}|{c.Danger}|{CastPatch.ActiveCount}";
            if (sig == _castDiagSig) return;
            _castDiagSig = sig;
            _services.Log.Info(
                $"[CastDiag] uuid={LastTargetUuid} casting={c.Casting} skillId={c.SkillId} name='{c.SkillName}' " +
                $"elapsed={c.ElapsedSec:F1}/{c.TotalSec:F1} danger={c.Danger} activeCasters={CastPatch.ActiveCount} " +
                $"patched={CastPatch.Installed}");
        }
        catch { /* diagnostic must never throw */ }
    }
}
