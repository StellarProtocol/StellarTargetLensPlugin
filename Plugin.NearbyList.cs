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

    private IReadOnlyList<NearbyMonsterTracker.NearbyEntry> NearbyDisplay()
    {
        int f = Time.frameCount;
        if (f == _nearbyDispFrame) return _nearbyDisp;
        _nearbyDispFrame = f;
        _nearbyDisp.Clear();

        var live = _nearby.Current;
        if (live.Count > 0) { _nearbyDisp.AddRange(live); return _nearbyDisp; }
        if (_services.Windows.IsLayoutEditing) _nearbyDisp.AddRange(ExampleNearby);
        return _nearbyDisp;
    }

    private bool NearbyRowVisible(int idx) => idx < NearbyDisplay().Count;

    // Row entity uuid (0 when out of range) — the click target for SetGameTarget and the highlight compare.
    private long NearbyUuid(int idx)
    {
        var l = NearbyDisplay();
        return idx >= 0 && idx < l.Count ? l[idx].Uuid : 0;
    }

    // True when this row is the player's current in-game target (highlight source). The layout-edit example rows carry
    // synthetic uuids (1..4) that never match a live CurrentTargetUuid, so they stay unhighlighted — intended.
    private bool NearbyIsTarget(int idx)
    {
        long u = NearbyUuid(idx);
        return u != 0 && u == _nearby.CurrentTargetUuid;
    }

    // Name (left, NoWrap → the cell hard-clips a long name, same as the threat rows). Rank-tinted via NearbyRowColor.
    private string NearbyName(int idx)
    {
        var l = NearbyDisplay();
        if (idx >= l.Count) return "";
        return StripTags(l[idx].Name);
    }

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

    // Name colour by rank: boss = red/orange accent, elite = yellow accent, normal = default (null → theme default).
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
}
