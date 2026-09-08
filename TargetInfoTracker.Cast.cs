using System;
using UnityEngine;

namespace Stellar.TargetLens;

/// <summary>
/// CAST BAR / channel ("吟唱/读条") read for the CURRENT target — HOOK-SOURCED via <see cref="CastPatch"/>. The patch
/// latches every caster's cast (keyed by caster uuid) on the one-shot <c>EntityExtensions.SetSingGuide</c> SETUP call
/// (value=0, maxValue=total seconds), and this read looks up the entry for <see cref="LastTargetUuid"/> and COUNTS UP
/// locally. Consequences vs the old widget scrape:
/// <list type="bullet">
///   <item>Works for ANY caster — boss, elite, or normal mob — not just entities that own a <c>ZUIBossBlood</c> frame.</item>
///   <item>The fill is a LOCAL count-up: <c>elapsed = now − StartTick</c>, <c>fraction = forward ? elapsed/total :
///         1 − elapsed/total</c> — SetSingGuide fires ONCE at cast start (value always 0) and the game tweens the bar
///         client-side without re-firing, so we can't read live progress off it; we tick it ourselves.</item>
///   <item>Clears when the cast runs its OWN duration: <see cref="CastPatch.TryGet"/> prunes an entry once
///         <c>elapsed ≥ TotalSec + grace</c> (SetSingGuide has no end call, so we run the bar to completion — interrupts
///         aren't observable and the bar simply finishes).</item>
///   <item>Skill id/name are best-effort enrichment (read off the ZEntity); when unknown the overlay shows "Casting…".</item>
/// </list>
///
/// <para>Frame-cached (one read per frame). Guarded — never throws.</para>
/// </summary>
internal sealed partial class TargetInfoTracker
{
    /// <summary>One frame's cast state off the current target. Count-UP: the bar fills as the cast progresses.
    /// <see cref="ElapsedSec"/>/<see cref="TotalSec"/> are seconds (SetSingGuide's maxValue is confirmed seconds);
    /// <see cref="Fraction"/> is the direction-corrected 0→1 fill.</summary>
    public readonly struct CastInfo
    {
        public readonly bool   Casting;
        public readonly int    SkillId;      // leveled cast id — drives the overlay icon (0 = unknown)
        public readonly string SkillName;    // "" when unknown → overlay shows "Casting…"
        public readonly bool   Danger;       // MonsterDanger cast → red accent
        public readonly float  ElapsedSec;   // seconds since cast start (locally ticked, clamped to TotalSec)
        public readonly float  TotalSec;     // total cast seconds (SetSingGuide maxValue)
        public readonly float  Fraction;     // count-UP bar fill 0→1 (direction-corrected)
        public CastInfo(bool casting, int skillId, string skillName, bool danger,
                        float elapsedSec, float totalSec, float fraction)
        {
            Casting = casting; SkillId = skillId; SkillName = skillName;
            Danger = danger; ElapsedSec = elapsedSec; TotalSec = totalSec; Fraction = fraction;
        }
    }

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
            if (uuid != 0 && CastPatch.TryGet(uuid, out var e))   // TryGet prunes an entry past its own duration
            {
                // Local count-UP: elapsed = now − StartTick. SetSingGuide's value is always 0 (setup-only), so the
                // game's own bar value is useless — we tick elapsed ourselves and derive the fraction. forward →
                // elapsed/total; reverse → 1 − elapsed/total. Guard a non-positive total (enrichment-only seed) → 0.
                float elapsed = 0f, frac = 0f;
                if (e.TotalSec > 0f)
                {
                    elapsed = (Environment.TickCount64 - e.StartTick) / 1000f;
                    if (elapsed < 0f) elapsed = 0f; else if (elapsed > e.TotalSec) elapsed = e.TotalSec;
                    float r = elapsed / e.TotalSec;
                    frac = e.Forward ? r : 1f - r;
                    if (frac < 0f) frac = 0f; else if (frac > 1f) frac = 1f;
                }
                _castCache = new CastInfo(true, e.SkillId, e.Name ?? "", e.Danger, elapsed, e.TotalSec, frac);
            }
        }
        catch { _castCache = default; }

        info = _castCache; return _castCache.Casting;
    }
}
