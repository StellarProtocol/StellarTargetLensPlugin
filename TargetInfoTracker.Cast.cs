using System;
using UnityEngine;

namespace Stellar.TargetLens;

/// <summary>
/// CAST BAR / channel ("吟唱/读条") read for the CURRENT target — HOOK-SOURCED via <see cref="CastPatch"/>. The patch
/// latches every non-local caster's in-flight cast (keyed by caster uuid) on each <c>EntityExtensions.SetSingGuide</c>
/// tick — the per-frame producer that carries the real bar values — and this read simply looks up the entry for
/// <see cref="LastTargetUuid"/>. Consequences vs the old widget scrape:
/// <list type="bullet">
///   <item>Works for ANY caster — boss, elite, or normal mob — not just entities that own a <c>ZUIBossBlood</c> frame.</item>
///   <item>The fill is the GAME'S OWN bar value (<c>forward ? value/max : 1 − value/max</c>) — no local elapsed tick.</item>
///   <item>Clears when the caster stops ticking: <see cref="CastPatch.TryGet"/> prunes an entry gone stale (~0.3s),
///         the stand-in for an explicit end call (SetSingGuide has none) — so an interrupt drops the bar promptly.</item>
///   <item>Skill id/name are best-effort enrichment; when unknown the overlay shows a generic "Casting…".</item>
/// </list>
///
/// <para>Frame-cached (one read per frame). Guarded — never throws.</para>
/// </summary>
internal sealed partial class TargetInfoTracker
{
    /// <summary>One frame's cast state off the current target. <see cref="Fraction"/> is the game's own bar fill 0→1.</summary>
    public readonly struct CastInfo
    {
        public readonly bool   Casting;
        public readonly int    SkillId;      // leveled cast id — drives the overlay icon (0 = unknown)
        public readonly string SkillName;    // "" when unknown → overlay shows "Casting…"
        public readonly bool   Danger;       // MonsterDanger cast → red accent
        public readonly float  Fraction;     // count-UP bar fill 0→1 (game's own value, direction-corrected)
        public CastInfo(bool casting, int skillId, string skillName, bool danger, float fraction)
        {
            Casting = casting; SkillId = skillId; SkillName = skillName;
            Danger = danger; Fraction = fraction;
        }
    }

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
            if (uuid != 0 && CastPatch.TryGet(uuid, out var e))   // TryGet prunes a stale (ended/interrupted) entry
            {
                // Count-UP fill straight from the game's bar value. forward → value/max; reverse → 1 − value/max.
                // Guard a non-positive max (an enrichment-only seed before the first SetSingGuide tick) → 0.
                float frac = 0f;
                if (e.MaxValue > 0f)
                {
                    float r = e.Value / e.MaxValue;
                    frac = e.Forward ? r : 1f - r;
                    if (frac < 0f) frac = 0f; else if (frac > 1f) frac = 1f;
                }
                _castCache = new CastInfo(true, e.SkillId, e.Name ?? "", e.Danger, frac);
            }
        }
        catch { _castCache = default; }

        LogCastDiag();
        info = _castCache; return _castCache.Casting;
    }

    // Change-gated [CastDiag] line (opt-in via CastDiag). Confirms the read tracks the hook: which target, whether it
    // is casting, the skill id/name, the count-up fraction, danger, and how many casters are live in the latch (so a
    // non-boss cast that fires the hook is visible even if it isn't the current target). Never throws.
    private void LogCastDiag()
    {
        if (!CastDiag) return;
        try
        {
            var c = _castCache;
            string sig = $"{LastTargetUuid}|{c.Casting}|{c.SkillId}|{c.SkillName}|{c.Fraction:F2}|{c.Danger}|{CastPatch.ActiveCount}";
            if (sig == _castDiagSig) return;
            _castDiagSig = sig;

            long ageMs = -1;
            if (LastTargetUuid != 0 && CastPatch.TryGet(LastTargetUuid, out var e))
                ageMs = Environment.TickCount64 - e.LastTickMs;

            _services.Log.Info(
                $"[CastDiag] uuid={LastTargetUuid} casting={c.Casting} skillId={c.SkillId} name='{c.SkillName}' " +
                $"frac={c.Fraction:F2} danger={c.Danger} lastAgeMs={ageMs} activeCasters={CastPatch.ActiveCount} " +
                $"patched={CastPatch.Installed}");
        }
        catch { /* diagnostic must never throw */ }
    }
}
