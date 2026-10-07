using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.TargetLens;

// Shared display data for the standalone Nearby Monsters window (Plugin.NearbyWindow.cs). Builds the frame-gated
// row list off NearbyMonsterTracker.Current (already alive-filtered, distance-capped, rank-then-distance sorted,
// capped at NearbySlots) and exposes the per-slot getters the window's rows bind to. Mirrors the threat display
// pattern (Plugin.TargetHud.Threat.cs): one poll per rendered frame, an example list for layout-edit placement.
public sealed partial class Plugin
{
    private bool _nearbyOn = true;   // config-backed: show the nearby-monster window (default ON)
    private bool _nearbyTargetableOnly = false;   // config-backed: cap the list by the player's per-class targetable range (default OFF)

    // Frame-gated display list: the tracker's Current (or the example while layout-editing with nothing live).
    private int _nearbyDispFrame = -1;
    private readonly List<NearbyMonsterTracker.NearbyEntry> _nearbyDisp = new();

    // Representative sample shown while the layout editor is active and no monsters are nearby (mirrors ExampleThreat).
    private static readonly NearbyMonsterTracker.NearbyEntry[] ExampleNearby =
    {
        new(1, "Dungeon Warden",   9000, 12000,  8f, "Boss"),
        new(2, "Elite Stalker",    3200,  4000, 15f, "Elite"),
        new(3, "Cave Prowler",      520,  1200, 22f, "Normal"),
        new(4, "Scurrying Vermin",  140,   300, 38f, "Normal"),
    };

    // Called from the window's ShouldRender AND every row Func, i.e. straight off the framework render loop — so it
    // is fully guarded: on ANY throw it returns the LAST good list (never propagates into the render loop, which is
    // the surface the "all HUDs freeze" regression corrupts). The tracker's Poll is already throttled + guarded.
    private IReadOnlyList<NearbyMonsterTracker.NearbyEntry> NearbyDisplay()
    {
        int f = Time.frameCount;
        if (f == _nearbyDispFrame) return _nearbyDisp;
        try
        {
            var live = _nearby.Current;   // may resolve a (throttled) poll — do this BEFORE touching _nearbyDisp
            _nearbyDispFrame = f;
            _nearbyDisp.Clear();
            if (live.Count > 0) { _nearbyDisp.AddRange(live); return _nearbyDisp; }
            if (_services.Windows.IsLayoutEditing) _nearbyDisp.AddRange(ExampleNearby);
            return _nearbyDisp;
        }
        catch
        {
            _nearbyDispFrame = f;      // mark done so we don't retry-thrash the throw within this frame
            return _nearbyDisp;         // last good list (or empty) — nothing escapes into the render loop
        }
    }

    private bool NearbyRowVisible(int idx) => idx < NearbyDisplay().Count;

    // Row entity uuid (0 when out of range) — the click target for SetGameTarget and the highlight compare.
    private long NearbyUuid(int idx)
    {
        var l = NearbyDisplay();
        return idx >= 0 && idx < l.Count ? l[idx].Uuid : 0;
    }

    // True when this row is the player's current in-game target (highlight source). Compares against the authoritative
    // current-target resolver (_targetInfo.Current.Uuid) — the SAME value the main Target HUD shows — so blue follows
    // BOTH the soft aim slot AND a hard lock. NearbyMonsterTracker.CurrentTargetUuid reads only AttrTargetId (30), the
    // aim slot, which clears on hard-lock (target then lives in AttrTargetUuid 450 / ZWorld.LockTargetUuid) — that was
    // why the blue bar fell off a locked monster. Current's getter is frame-gated, so touching it here is fresh + cheap.
    // The layout-edit example rows carry synthetic uuids (1..4) that never match a live target uuid → stay unhighlighted.
    private bool NearbyIsTarget(int idx)
    {
        long u = NearbyUuid(idx);
        return u != 0 && u == _targetInfo.Current.Uuid;
    }

    // Monster name — rendered as the HP bar's inside-LEFT primary label (Plugin.NearbyWindow.cs). The framework's
    // Modern-bar overlay label uses HorizontalWrapMode.Overflow and is NOT masked to the track (only the fill clip
    // is), so a long name would SPILL past the bar, over the HP% and off the edge. We therefore pre-truncate here to
    // a width-aware budget and append an ellipsis — the only overflow-proof option for this label path.
    private string NearbyName(int idx)
    {
        var l = NearbyDisplay();
        if (idx >= l.Count) return "";
        return TruncateNameToBar(StripTags(l[idx].Name));
    }

    // Name colour by rank: boss = red/orange accent, elite = yellow accent, normal = default (null → theme default).
    // Read off the same frame-gated display list as the other getters, so a recycled row re-tints when its live
    // occupant changes rank (boss→elite→normal). Wired to the HP bar's primary inside label via LabelColorFn.
    private ColorRgba? NearbyRowColor(int idx)
    {
        var l = NearbyDisplay();
        if (idx >= l.Count) return null;
        return l[idx].Rank switch
        {
            "Boss"  => NearbyBossColor,
            "Elite" => NearbyEliteColor,
            _        => (ColorRgba?)null,
        };
    }

    // Width-aware ellipsis clip for the in-bar name. Geometry mirrors BuildNearbyWindowRoot's row at the live
    // _nearbyScale and reads the live window width (the window is width-resizable 240–700), so the budget tracks the
    // actual layout. The available text width = window width − distance cell − paddings − the track's inside insets −
    // room reserved for the right-side HP%. We then walk the name charging each glyph a conservative width (full-width
    // CJK/kana/hangul ≈ 1× the font px, everything else ≈ 0.65×) and stop before the budget, erring WIDE plus an
    // ellipsis reserve + a safety slack so the result cannot overflow for any realistic monster name.
    private string TruncateNameToBar(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        float sc = _nearbyScale;
        // Live window width; fall back to the default 320*sc before the first layout pass / on a 0 rect.
        float w = _nearbyWindow is { } win ? win.Rect.Width : 0f;
        if (w <= 0f) w = 320f * sc;
        float fontPx    = (int)Math.Round(12 * sc);         // the bar's LabelFontSize (barFont)
        float distCellW = 44f * sc;
        float rowGap    = 6f * sc;
        const float colPad = 16f;                            // ColumnElement Padding (8 each side, unscaled at build)
        const float insets = 10f;                            // AddOverlayText's 5px left+right inset inside the track
        float barTrack  = w - colPad - distCellW - rowGap;   // the bar cell's width (Weight:1 fills the rest of the row)
        float pctReserve = 4f * fontPx * 0.65f + 8f;         // keep clear of the right-side HP% ("100%" worst case) + a gap
        float avail = barTrack - insets - pctReserve - fontPx /*ellipsis*/ - 6f /*slack*/;
        if (avail <= 0f) return "…";

        float used = 0f;
        for (int i = 0; i < name.Length; i++)
        {
            float cw = IsWideGlyph(name[i]) ? fontPx : fontPx * 0.65f;
            if (used + cw > avail) return name.Substring(0, i) + "…";
            used += cw;
        }
        return name;   // whole name fits
    }

    // East-Asian "wide" (full-width) test: CJK/kana/hangul/fullwidth-forms glyphs render ~1× the font px where Latin
    // is ~0.5–0.65×. Used ONLY to size the name-truncation budget conservatively, never for the text's correctness.
    private static bool IsWideGlyph(char c)
        => c >= 0x1100 && (
               c <= 0x115F ||                                   // Hangul Jamo
               c == 0x2329 || c == 0x232A ||
               (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F) ||   // CJK radicals .. Yi
               (c >= 0xAC00 && c <= 0xD7A3) ||                  // Hangul syllables
               (c >= 0xF900 && c <= 0xFAFF) ||                  // CJK compat ideographs
               (c >= 0xFE30 && c <= 0xFE4F) ||                  // CJK compat forms
               (c >= 0xFF00 && c <= 0xFF60) ||                  // Fullwidth forms
               (c >= 0xFFE0 && c <= 0xFFE6));

    // Bar fill = current HP fraction (clamped). MaxHp <= 0 → empty (shouldn't happen — the tracker filters alive only).
    private float NearbyFraction(int idx)
    {
        var l = NearbyDisplay();
        if (idx >= l.Count) return 0f;
        var e = l[idx];
        return e.MaxHp > 0 ? Clamp01((float)e.Hp / e.MaxHp) : 0f;
    }

    // HP percent shown inside the bar.
    private string NearbyHpPct(int idx)
    {
        var l = NearbyDisplay();
        if (idx >= l.Count) return "";
        var e = l[idx];
        if (e.MaxHp <= 0) return "";
        return $"{(int)Math.Round(Clamp01((float)e.Hp / e.MaxHp) * 100f)}%";
    }

    // Distance readout (right cell), whole metres, e.g. "42m".
    private string NearbyDist(int idx)
    {
        var l = NearbyDisplay();
        if (idx >= l.Count) return "";
        return $"{(int)Math.Round(l[idx].Distance)}m";
    }
}
