using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.TargetLens;

// Data layer for the Boss Skill Timers overlay (Plugin.BossTimerWindow.cs). Owns the overlay's OWN icon-UV pool
// (separate from the Target HUD tile pool and the buff-list pool so none of them stomp each other's atlas rects)
// and the per-row getters the window binds to. The rows come from BossDbmTracker.Current (the game's DBM list);
// in layout-edit mode with no live boss, a small example list is shown so the window can be sized/placed.
public sealed partial class Plugin
{
    private readonly UvRect[] _bossTimerUv = new UvRect[BossTimerSlots];   // OWN pool — do NOT share other windows' pools

    // Layout-edit sample: two upcoming skills mid-countdown so the rows have real content to size/place against.
    private static readonly IReadOnlyList<DbmEntry> BossTimerExample = new[]
    {
        new DbmEntry(0, "Mighty Bite",           8f, 10f),
        new DbmEntry(0, "Opportunistic Pounce", 18f, 20f),
    };

    // The rows to render: the live DBM list, or the example list while layout-editing with nothing live.
    private IReadOnlyList<DbmEntry> BossTimerRows()
    {
        var live = _bossDbm.Current;
        if (live.Count > 0) return live;
        return _services.Windows.IsLayoutEditing ? BossTimerExample : live;
    }

    // A row renders only when a live/example entry occupies its slot.
    private bool BossTimerRowVisible(int idx) => idx < BossTimerRows().Count;

    // ── Per-row getters (all guarded; never throw) ───────────────────────────────

    // Skill icon is BEST-EFFORT (the game's own DBM row has none): try LoadSkillIcon(DbmId) then LoadImagineIcon.
    // On any miss the UV is cleared and null returned, so the row simply shows no icon (its cell stays sized).
    private object? GetBossTimerIcon(int idx)
    {
        var list = BossTimerRows();
        if (idx >= list.Count) { _bossTimerUv[idx] = default; return null; }
        int id = list[idx].DbmId;
        if (id <= 0) { _bossTimerUv[idx] = default; return null; }
        var sk = _services.GameAssets.LoadSkillIcon(id, out _bossTimerUv[idx]);
        if (sk != null) return sk;
        return _services.GameAssets.LoadImagineIcon(id, out _bossTimerUv[idx]);
    }

    // Name inside the bar (left). Tag-stripped and ellipsised so it can't collide with the right-aligned MM:SS.
    // The window is width-resizable, so the char budget scales with the current width (mirrors EffTilesPerLine's
    // width-aware pattern) — drag wider to reveal more of a long skill name.
    private string BossTimerName(int idx)
    {
        var list = BossTimerRows();
        if (idx >= list.Count) return "";
        return Truncate(StripTags(list[idx].Name), BossTimerNameBudget());
    }

    // Name char budget derived from the current window width. Available name pixels ≈ Width − padding(16) −
    // icon cell(22) − gap(6) − MM:SS reserve(~48); at ~7.6px per char (font 13). At the 260 default this yields
    // ~22 chars — the same as the old fixed BuffListNameBudget, so the default look is unchanged.
    private int BossTimerNameBudget()
    {
        float w = _bossTimerWindow != null ? _bossTimerWindow.Rect.Width : 0f;
        if (w < 1f) w = 260f;                                   // pre-mount fallback = default width
        int budget = (int)((w - 92f) / 7.6f);
        return System.Math.Clamp(budget, 8, 80);
    }

    // MM:SS countdown inside the bar (right), zero-padded to match the game (e.g. 00:08, 01:05). Floors the
    // remaining seconds (a decreasing timer shows "00:08" through the whole 8th second).
    private string BossTimerTime(int idx)
    {
        var list = BossTimerRows();
        if (idx >= list.Count) return "";
        int total = (int)list[idx].RemainSec;
        if (total < 0) total = 0;
        return $"{total / 60:00}:{total % 60:00}";
    }

    // Bar fill = remaining-time fraction (soonest cast drains toward 0). TotalSec ≤ 0 → empty.
    private float BossTimerFraction(int idx)
    {
        var list = BossTimerRows();
        if (idx >= list.Count) return 0f;
        var e = list[idx];
        return e.TotalSec > 0f ? Clamp01(e.RemainSec / e.TotalSec) : 0f;
    }
}
