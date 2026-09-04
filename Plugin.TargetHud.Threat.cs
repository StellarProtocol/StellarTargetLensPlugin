using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.TargetLens;

// Per-player THREAT / AGGRO block for the Target HUD. Renders a compact Top-N list of who holds aggro on the
// current monster ("Tank 62% / You 23% …"), driven by the target's AttrHateList (474) — see
// TargetInfoTracker.Threat.cs. Opt-in (default OFF) via the "Show threat / aggro list" setting; hidden whenever
// the list is empty. The local player's row is accent-coloured, and if the local player isn't in the Top-N their
// own row is appended so their standing is always visible.
//
// HUD-HEIGHT NOTE: the Target HUD window locks its height (Resizable + MinHeight==MaxHeight) so SetRect can't grow
// it at runtime (WindowRenderer clamps SetRect height to the spec's Min/Max, which are equal). So the extra height
// for this block is reserved at RegisterTargetHud time FROM the persisted show_threat config (see ThreatReserveH).
// A runtime toggle persists the flag and fully re-fits the panel on the next load; until then the block simply
// won't have reserved space if it was toggled on mid-session (documented behaviour, not a clip risk — the block is
// gated hidden when there's no data, and the toggle default is off at load).
public sealed partial class Plugin
{
    private bool _showThreat;   // config-backed: show the threat/aggro block (default OFF)
    private bool _threatDiag;   // config-backed: TEMPORARY threat-read diagnostic logging

    private const int   ThreatTopN   = 4;      // number of highest-aggro rows shown
    private const int   ThreatSlots   = ThreatTopN + 1; // + 1 for the local player's appended row
    // Vertical space the block needs when shown; reserved into the locked window height at registration time.
    // Separator + "Threat" header + ThreatSlots rows (~20px each) + gaps, with headroom so rows never clip.
    private const float ThreatReserveH = 150f;

    private static readonly ColorRgba ThreatFill       = new(0.85f, 0.45f, 0.30f, 1f);  // amber aggro bar
    private static readonly ColorRgba ThreatLocalColor = new(1.0f, 0.92f, 0.45f, 1f);   // gold — the local player's row

    // Frame-gated display list: the Top-N of TryGetThreatList, plus the local player's row appended when they fall
    // outside the Top-N (so up to ThreatSlots rows). Built once per frame; the slot getters read it back.
    private int _threatDispFrame = -1;
    private readonly List<TargetInfoTracker.ThreatEntry> _threatDisp = new();

    // Representative sample shown while the layout editor is active and no monster is locked (mirrors ExampleEffects).
    private static readonly TargetInfoTracker.ThreatEntry[] ExampleThreat =
    {
        new(1, "Vanguard",  8000, 0.62f, false),
        new(2, "You",       3000, 0.23f, true),
        new(3, "Mender",    1400, 0.11f, false),
        new(4, "Arcanist",   500, 0.04f, false),
    };

    private IReadOnlyList<TargetInfoTracker.ThreatEntry> ThreatDisplay()
    {
        int f = Time.frameCount;
        if (f == _threatDispFrame) return _threatDisp;
        _threatDispFrame = f;
        _threatDisp.Clear();

        if (ShowExample) { _threatDisp.AddRange(ExampleThreat); return _threatDisp; }

        _ = Cur;   // poll _targetInfo this frame so LastTargetEntity is fresh before the threat read
        if (_targetInfo.TryGetThreatList(out var full) && full.Count > 0)
        {
            int n = Math.Min(ThreatTopN, full.Count);
            bool localShown = false;
            for (int i = 0; i < n; i++)
            {
                _threatDisp.Add(full[i]);
                if (full[i].IsLocal) localShown = true;
            }
            // Local player not in the Top-N but present further down → append their own row so standing is visible.
            if (!localShown)
                for (int i = n; i < full.Count; i++)
                    if (full[i].IsLocal) { _threatDisp.Add(full[i]); break; }
        }
        return _threatDisp;
    }

    // The block shows only when the toggle is on AND there is data (ConditionalElement with no Else collapses to
    // zero height otherwise, so no empty rows draw).
    private HudElement BuildTargetHudThreatBlock()
    {
        var rows = new HudElement[ThreatSlots + 2];
        rows[0] = new SeparatorElement();
        rows[1] = new TextElement(() => "Threat", Color: MutedColor, Emphasis: true, FontSize: 14);
        for (int s = 0; s < ThreatSlots; s++)
        {
            int idx = s;
            var row = new RowElement(new HudElement[]
            {
                new CellElement(
                    new TextElement(() => ThreatName(idx), Color: () => ThreatRowColor(idx), FontSize: 14, NoWrap: true),
                    Weight: 1f),
                new CellElement(
                    new BarElement(() => ThreatFraction(idx), ThreatFill, () => ThreatPct(idx))
                    { Style = BarStyle.Modern, Height = 14f, FillWidth = true, LabelFontSize = 12, LabelInside = true },
                    Width: 96f),
            }, Gap: 6f);
            rows[s + 2] = new ConditionalElement(() => ThreatRowVisible(idx), row);
        }

        return new ConditionalElement(
            () => _showThreat && ThreatDisplay().Count > 0,
            new ColumnElement(rows, Gap: 3f));
    }

    private bool ThreatRowVisible(int idx) => idx < ThreatDisplay().Count;

    // Name (accent-marked for the local player with a leading ▶). NoWrap on the element hard-clips a long name.
    private string ThreatName(int idx)
    {
        var l = ThreatDisplay();
        if (idx >= l.Count) return "";
        var e = l[idx];
        return e.IsLocal ? "▶ " + e.Name : e.Name;
    }

    private string ThreatPct(int idx)
    {
        var l = ThreatDisplay();
        if (idx >= l.Count) return "";
        return $"{(int)Math.Round(l[idx].Pct * 100f)}%";
    }

    private float ThreatFraction(int idx)
    {
        var l = ThreatDisplay();
        if (idx >= l.Count) return 0f;
        return Clamp01(l[idx].Pct);
    }

    // Local player's row is gold; everyone else uses the default text colour (null → theme default).
    private ColorRgba? ThreatRowColor(int idx)
    {
        var l = ThreatDisplay();
        if (idx >= l.Count) return null;
        return l[idx].IsLocal ? ThreatLocalColor : (ColorRgba?)null;
    }
}
