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

    private const int BuffListSlots = 16;                                  // fixed row pool (max rows at full height)
    private readonly UvRect[] _buffListUv = new UvRect[BuffListSlots];      // OWN pool — do NOT share the tile pool's _hudEffUv
    // Crest bar (20) + row gap (3) at scale 1; one row's vertical footprint. Scaled by _listScale so the height-driven
    // row count stays correct as rows grow — BuffListVisibleRows floors the window height by this stride.
    private float BuffRowStride => 23f * _listScale;
    private const int   BuffListNameBudget = 22;                          // name chars before it's ellipsised (leaves room for the time)
    private const string BuffListMarker    = "★ ";                         // leading "yours" marker on self-cast rows

    // Buff base-ids that must resolve to their OWN table icon, bypassing the source-skill icon step below. These are
    // the boss enrage-TIMER buff family ("Power Sealed" and siblings) applied by a boss mechanic skill: each buff's
    // own table icon is correct (buff_talent_skill_330301) but the applying skill's icon is unrelated, so the normal
    // source-skill-first order picks the wrong art. All ten share the buff_talent_skill_330301 (enrage-timer) icon
    // (verified against the game's BuffTable data).
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
    // More window height ⇒ more visible rows (the window is height-resizable): subtract the chrome padding + the Crest
    // caption block, then floor by the per-row stride (+ one gap: the last row has none below it).
    private int BuffListVisibleRows()
    {
        float h = _buffListWindow != null ? _buffListWindow.Rect.Height : 0f;
        if (h < 1f) h = BuffListFixedH + 8f * BuffRowStride;   // pre-mount fallback ≈ 8 rows
        int rows = (int)((h - BuffListFixedH + 3f * _listScale) / BuffRowStride);
        return System.Math.Clamp(rows, 1, BuffListSlots);
    }

    // ── List order (Crest): debuffs first, then buffs, each in arrival order. Frame-gated, reuses one list. ──
    private int _listOrderFrame = -1;
    private readonly List<TargetBuffRow> _listOrder = new();
    private int _listDebuffs, _listBuffs;

    private IReadOnlyList<TargetBuffRow> ListEffects()
    {
        int f = UnityEngine.Time.frameCount;
        if (f == _listOrderFrame) return _listOrder;
        _listOrderFrame = f;
        _listOrder.Clear();
        var all = CurEffects();
        for (int i = 0; i < all.Count; i++) if (all[i].BuffType == 0) _listOrder.Add(all[i]);
        _listDebuffs = _listOrder.Count;
        for (int i = 0; i < all.Count; i++) if (all[i].BuffType != 0) _listOrder.Add(all[i]);
        _listBuffs = _listOrder.Count - _listDebuffs;
        return _listOrder;
    }

    // A row renders only when it has a live effect AND fits at the current window height.
    private bool BuffListRowVisible(int idx) => idx < BuffListVisibleRows() && idx < ListEffects().Count;

    // Caption hint "2 debuffs · 2 buffs": rebuilt only when a count or the language template changes.
    private int _blHintD = -1, _blHintB = -1; private string? _blHintTpl; private string _blHint = "";
    private string BuffListHint()
    {
        ListEffects();
        string tpl = _loc.T("tl.hint.effects");
        if (_listDebuffs == _blHintD && _listBuffs == _blHintB && ReferenceEquals(tpl, _blHintTpl)) return _blHint;
        _blHintD = _listDebuffs; _blHintB = _listBuffs; _blHintTpl = tpl;
        _blHint = string.Format(tpl, _listDebuffs, _listBuffs);
        return _blHint;
    }

    // ── Per-row getters (all guarded; never throw) ───────────────────────────────

    private object? GetBuffListIcon(int idx)
    {
        var list = ListEffects();
        if (idx >= list.Count) { _buffListUv[idx] = default; return null; }
        return EffectIcon(list[idx], 20f * _listScale, out _buffListUv[idx]);   // icon-less → placeholder inside the ring
    }

    // ★-marker predicate: true when the row's effect was cast by the LOCAL PLAYER (self-cast). SAME predicate as
    // the classic tiles' HudEffectIsMine — FireUuid matches the local entity id (guarded; local==0 → false).
    private bool BuffListIsMine(int idx)
    {
        var list = ListEffects();
        if (idx >= list.Count) return false;
        long local = _services.CombatSnapshot.LocalEntityId.Value;
        return local != 0 && list[idx].FireUuid == local;
    }

    // Name inside the bar (left): "★ " (self-cast) then "{Layer}x " (2+ stacks), then the resolved name truncated
    // against a budget already reduced by the prefix. Cached per slot: rebuilt only when the row's effect, its stack
    // count or the self-cast flag changes (the resolved name is stable per effect), so polls don't allocate.
    private readonly TargetBuffRow?[] _blNameRow = new TargetBuffRow?[BuffListSlots];
    private readonly int[] _blNameKey = new int[BuffListSlots];
    private readonly string[] _blName = NewStrArray(BuffListSlots);
    private string BuffListName(int idx)
    {
        var list = ListEffects();
        if (idx >= list.Count) return "";
        var r = list[idx];
        bool mine = BuffListIsMine(idx);
        int key = r.Layer * 2 + (mine ? 1 : 0);
        if (ReferenceEquals(r, _blNameRow[idx]) && key == _blNameKey[idx]) return _blName[idx];
        string prefix = mine ? BuffListMarker : "";
        if (r.Layer >= 2) prefix += $"{r.Layer}x ";
        int budget = System.Math.Max(BuffListNameBudget - prefix.Length, 4);
        _blNameRow[idx] = r; _blNameKey[idx] = key;
        _blName[idx] = prefix + Truncate(StripTags(ResolveEffectName(r)), budget);
        return _blName[idx];
    }

    // Compact remaining time inside the bar (right): hours / minutes / seconds; permanent (RemainSec < 0) → "—".
    // Cached per slot by (unit, value) so the 10 Hz poll only builds a string when the shown value changes.
    private readonly int[] _blTimeKey = NewIntArray(BuffListSlots, int.MinValue);
    private readonly string[] _blTime = NewStrArray(BuffListSlots);
    private string BuffListTime(int idx)
    {
        var list = ListEffects();
        if (idx >= list.Count) return "";
        float rem = list[idx].RemainSec;
        int key = rem < 0f ? -1 : rem >= 3600f ? 2_000_000 + (int)(rem / 3600f) : rem >= 60f ? 1_000_000 + (int)(rem / 60f) : (int)rem;
        if (key == _blTimeKey[idx]) return _blTime[idx];
        _blTimeKey[idx] = key;
        _blTime[idx] = rem < 0f ? "—" : rem >= 3600f ? $"{(int)(rem / 3600f)}h" : rem >= 60f ? $"{(int)(rem / 60f)}m" : $"{(int)rem}s";
        return _blTime[idx];
    }

    // Bar fill = remaining-time fraction (same as the tiles' HudEffectFraction). Permanent = full.
    private float BuffListFraction(int idx)
    {
        var list = ListEffects();
        if (idx >= list.Count) return 0f;
        var r = list[idx];
        return r.Duration > 0 ? Clamp01(r.RemainSec / (r.Duration / 1000f)) : 1f;
    }

    // A permanent effect's full bar is drawn at ~55 % alpha (FillColorFn multiplies the gradient); others untinted.
    private static readonly ColorRgba PermanentTint = new(1f, 1f, 1f, 0.55f);
    private ColorRgba? BuffListTint(int idx)
    {
        var list = ListEffects();
        return idx < list.Count && (list[idx].Duration <= 0 || list[idx].RemainSec < 0f) ? PermanentTint : null;
    }

    // Debuff (BuffType == 0) → the orange ring + debuff bar; everything else → green.
    private bool BuffListIsDebuff(int idx)
    {
        var list = ListEffects();
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
