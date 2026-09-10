using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Data layer for the "List" buff/debuff STYLE — the standalone Target Effects window (Plugin.BuffListWindow.cs).
// Owns the buff-display-style config field, its OWN icon-UV pool (separate from the Target HUD tile pool so the two
// styles can be mounted at once without stomping each other's atlas rects), and the per-row getters the window's
// rows bind to. Reuses the Target HUD's effect resolution (CurEffects / ResolveEffectName) so the per-source
// (mine/others/monster) and ShowHidden filters and the layout-edit example data apply to the list automatically — the shared icon-priority
// logic is extracted here as ResolveEffectIcon and called by BOTH the classic tiles and this list.
public sealed partial class Plugin
{
    // Buff/debuff display style: 0 = Classic (tiles inside the Target HUD), 1 = List (this window, the default),
    // 2 = Off (no buff/debuff display at all — neither tiles nor the list window).
    // Loaded in RegisterBuffListWindow (mirrors how _showThreat is loaded in RegisterThreatWindow).
    private int _buffStyle = 1;

    // Target Effects LIST row-size multiplier (List style ONLY; Classic tiles ignore it). Scales the row's icon px,
    // icon cell width, bar height, label font, and the row stride together so the list grows coherently. Loaded in
    // RegisterBuffListWindow alongside _buffStyle; changed live via SetListScale, which rebuilds the window Root
    // because the element sizes are baked at build time. Default 1.0 = unchanged.
    private float _listScale = 1f;

    // Dropdown option order MUST match the style ints above (index 0 = Classic, 1 = List, 2 = Off).
    // Built fresh from the loc catalog each call so the labels follow the active language (the dropdown's
    // Options provider re-invokes it), not baked once at init.
    private string[] BuffStyleOptions() => new[]
    {
        _loc.T("tl.style.classic"),
        _loc.T("tl.style.list"),
        _loc.T("tl.style.off"),
    };

    private const int BuffListSlots = 16;                                  // fixed row pool (max rows at full height)
    private readonly UvRect[] _buffListUv = new UvRect[BuffListSlots];      // OWN pool — do NOT share the tile pool's _hudEffUv
    // bar (18) + column gap (3) at scale 1; one row's vertical footprint. Scaled by _listScale so the height-driven
    // row count stays correct as rows grow — BuffListVisibleRows floors the window height by this stride.
    private float BuffRowStride => 21f * _listScale;
    private const int   BuffListNameBudget = 22;                          // name chars before it's ellipsised (leaves room for the time)
    private const string BuffListMarker    = "★ ";                         // leading "yours" marker on self-cast rows

    // Buff base-ids that must resolve to their OWN table icon, bypassing the source-skill icon step below. These are
    // the boss enrage-TIMER buff family ("Power Sealed" and siblings) applied by a boss mechanic skill: each buff's
    // own table icon is correct (buff_talent_skill_330301) but the applying skill's icon is unrelated, so the normal
    // source-skill-first order picks the wrong art. All ten share the buff_talent_skill_330301 (enrage-timer) icon
    // (verified against Resources/BuffTable.json).
    private static readonly HashSet<int> ForceOwnBuffIcon = new()
    {
        501706, 501710, 501712, 501714, 851388, 880803, 881613, 974342, 995191, 2100106,
    };

    // Shared icon-priority resolution used by BOTH the classic tiles (GetHudEffectIcon) and the list rows
    // (GetBuffListIcon). The source-skill icon swap is meaningful ONLY for a PLAYER-applied effect — for those we
    // want the applying class-skill's art (e.g. "your Firestorm" shows your skill icon). A boss's OWN buff (source =
    // the current target monster) or an effect with an UNKNOWN source (no caster) has no meaningful player skill to
    // swap to, so its own table icon is the correct art. Order:
    //   1. manual override (highest) → Imagine icon on the configured skill.
    //   2. own-icon: ForceOwnBuffIcon safety net OR IsOwnOrUnknownSource (boss-own / no-caster) → the buff's OWN icon.
    //   3. player-applied swap: source skill → Imagine icon, then skill icon.
    //   4. fallback → the buff's own icon.
    // LoadImagineIcon is tried DIRECTLY on the source skill (it misses leveled imagine cast ids when gated on
    // GetImagineForSkill); fall through on null so an effect whose skill icon doesn't load still shows its buff icon.
    private object? ResolveEffectIcon(TargetBuffRow r, out UvRect uv)
    {
        uv = default;
        // 1. Manual override wins outright.
        if (EffectOverrides.TryGetValue(r.BaseId, out var ov))
            return _services.GameAssets.LoadImagineIcon(ov.IconSkill, out uv);
        // 2. Boss-own / unknown source (or the explicit enrage-timer safety net) → the buff's OWN table icon; no swap.
        if (ForceOwnBuffIcon.Contains(r.BaseId) || IsOwnOrUnknownSource(r))
            return _services.GameAssets.LoadBuffIcon(r.BaseId, out uv);
        // 3. Player-applied effect → prefer the applying skill's icon.
        if (r.SkillId > 0)
        {
            var img = _services.GameAssets.LoadImagineIcon(r.SkillId, out uv);
            if (img != null) return img;
            var sk = _services.GameAssets.LoadSkillIcon(r.SkillId, out uv);
            if (sk != null) return sk;
        }
        // 4. Fallback.
        return _services.GameAssets.LoadBuffIcon(r.BaseId, out uv);
    }

    // True when the effect's caster is the current target monster ITSELF or is unknown (no caster). Guarded: a 0
    // FireUuid (no caster) always counts; a 0 target uuid (no target / not yet resolved) means only the no-caster
    // case is caught. The uuid>>16 compare matches the entity's config id so a boss whose per-instance uuid differs
    // from its selected-target uuid still resolves as "own". A PLAYER-cast effect (mine or another player's) has a
    // non-zero FireUuid that is neither the target nor shares its config id, so it correctly falls through to the swap.
    private bool IsOwnOrUnknownSource(TargetBuffRow r)
    {
        long tgt = _targetInfo != null ? _targetInfo.LastTargetUuid : 0;
        return r.FireUuid == 0
            || (tgt != 0 && (r.FireUuid == tgt || (r.FireUuid >> 16) == (tgt >> 16)));
    }

    // ── Height-driven row count ──────────────────────────────────────────────────
    // More window height ⇒ more visible rows (the window is height-resizable). Mirrors the threat window's
    // height-based sizing: subtract the title line + column padding, then floor by the per-row stride.
    private int BuffListVisibleRows()
    {
        float h = _buffListWindow != null ? _buffListWindow.Rect.Height : 0f;
        if (h < 1f) h = 208f;                       // pre-mount fallback ≈ the default (8-row) height
        const float titleReserve = 24f;             // title TextElement (~14px) + its column gap
        const float padding      = 16f;             // ColumnElement padding (8 top + 8 bottom)
        int rows = (int)((h - titleReserve - padding) / BuffRowStride);
        return System.Math.Clamp(rows, 1, BuffListSlots);
    }

    // A row renders only when it has a live effect AND fits at the current window height.
    private bool BuffListRowVisible(int idx) => idx < BuffListVisibleRows() && idx < CurEffects().Count;

    // ── Per-row getters (all guarded; never throw) ───────────────────────────────

    private object? GetBuffListIcon(int idx)
    {
        var list = CurEffects();
        if (idx >= list.Count) { _buffListUv[idx] = default; return null; }
        return ResolveEffectIcon(list[idx], out _buffListUv[idx]);
    }

    // ★-marker predicate: true when the row's effect was cast by the LOCAL PLAYER (self-cast). SAME predicate as
    // the classic tiles' HudEffectIsMine — FireUuid matches the local entity id (guarded; local==0 → false). Lets a
    // user tell which effects THEY applied when "Show effects others applied" is on.
    private bool BuffListIsMine(int idx)
    {
        var list = CurEffects();
        if (idx >= list.Count) return false;
        long local = _services.CombatSnapshot.LocalEntityId.Value;
        return local != 0 && list[idx].FireUuid == local;
    }

    // Name inside the bar (left). Tag-stripped and ellipsised so it can't collide with the right-aligned time.
    // Prefixes, in order: "★ " (self-cast, same star meaning as the classic tiles) then "{Layer}x " when the effect
    // has 2+ stacks (same Layer field the classic tiles badge with ×N; never shown for 0/1 so we don't print "1x").
    // The full prefix is built FIRST, then the resolved name is truncated against a budget already reduced by the
    // prefix width (clamped to a small minimum), so the markers can never push the name into the right-aligned time.
    private string BuffListName(int idx)
    {
        var list = CurEffects();
        if (idx >= list.Count) return "";
        string prefix = BuffListIsMine(idx) ? BuffListMarker : "";
        int layer = list[idx].Layer;
        if (layer >= 2) prefix += $"{layer}x ";
        int budget = System.Math.Max(BuffListNameBudget - prefix.Length, 4);
        string name = Truncate(StripTags(ResolveEffectName(list[idx])), budget);
        return prefix + name;
    }

    // Compact remaining time inside the bar (right): hours / minutes / seconds; permanent (RemainSec < 0) → blank.
    private string BuffListTime(int idx)
    {
        var list = CurEffects();
        if (idx >= list.Count) return "";
        float rem = list[idx].RemainSec;
        if (rem < 0f) return "";                                   // permanent
        if (rem >= 3600f) return $"{(int)(rem / 3600f)}h";
        if (rem >= 60f)   return $"{(int)(rem / 60f)}m";
        return $"{(int)rem}s";
    }

    // Bar fill = remaining-time fraction (same as the tiles' HudEffectFraction). Permanent = full.
    private float BuffListFraction(int idx)
    {
        var list = CurEffects();
        if (idx >= list.Count) return 0f;
        var r = list[idx];
        return r.Duration > 0 ? Clamp01(r.RemainSec / (r.Duration / 1000f)) : 1f;
    }

    // Bar colour by type: debuff (BuffType == 0) → red/orange, everything else → green. Drives the per-row
    // Then/Else bar split in the window root (BarElement's colour arg is a VALUE, not a Func).
    private bool BuffListIsDebuff(int idx)
    {
        var list = CurEffects();
        if (idx >= list.Count) return false;
        return list[idx].BuffType == 0;
    }

    // Length-cap with a trailing ellipsis (no ellipsis facility on TextElement, so trim the string itself).
    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s.Substring(0, max - 1).TrimEnd() + "…";
    }
}
