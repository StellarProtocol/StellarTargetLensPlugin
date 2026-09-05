using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.TargetLens;

// Shared per-player THREAT / AGGRO display data for the standalone Threat window (Plugin.ThreatWindow.cs). Builds
// the frame-gated Top-N display list ("Tank 62% / You 23% …") off the current monster's AttrHateList (474) — see
// TargetInfoTracker.Threat.cs — and exposes the per-slot getters the window's rows bind to. The local player's row
// is accent-coloured, and if the local player isn't in the Top-N their own row is appended so their standing is
// always visible. The window (not this file) owns visibility gating on the "Show threat / aggro list" toggle.
public sealed partial class Plugin
{
    private bool _showThreat;   // config-backed: show the threat/aggro window (default OFF)
    private bool _threatDiag;   // config-backed: TEMPORARY threat-read diagnostic logging

    private const int   ThreatTopN   = 4;      // number of highest-aggro rows shown
    private const int   ThreatSlots   = ThreatTopN + 1; // + 1 for the local player's appended row

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
