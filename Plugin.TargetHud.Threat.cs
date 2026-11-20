using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.TargetLens;

// Shared per-player THREAT / AGGRO display data for the standalone Threat window (Plugin.ThreatWindow.cs). Builds
// the frame-gated Top-N display list ("Tank 62% / You 23% …") off the current monster's AttrHateList (474) — see
// TargetInfoTracker.Threat.cs — and exposes the per-slot getters the window's rows bind to. The local player's row
// carries the pale-gold ring (Crest), and if the local player isn't in the Top-N their own row is appended so their standing is
// always visible. The window (not this file) owns visibility gating on the "Show threat / aggro list" toggle.
public sealed partial class Plugin
{
    private bool _showThreat = true;   // config-backed: show the threat/aggro window (default ON)

    // Threat display mode (config-backed, loaded in RegisterThreatWindow): 0 = Top aggro (only the single highest-threat
    // holder), 1 = Aggro List (Top-N + the local player's own appended row — the default / original behaviour).
    private int _threatMode = 1;

    private const int   ThreatTopN   = 4;      // number of highest-aggro rows shown
    private const int   ThreatSlots   = ThreatTopN + 1; // + 1 for the local player's appended row

    // Real rank (1-based position in the full hate list) of each displayed slot — the appended "you" row keeps its own
    // rank (e.g. 7). Filled with the display list; read by the medallion. _threatTotal = players on the hate list.
    private readonly int[] _threatRank = new int[ThreatSlots];
    private int _threatTotal;

    // Frame-gated display list: the Top-N of TryGetThreatList, plus the local player's row appended when they fall
    // outside the Top-N (so up to ThreatSlots rows). Built once per frame; the slot getters read it back.
    private int _threatDispFrame = -1;
    private readonly List<TargetInfoTracker.ThreatEntry> _threatDisp = new();

    // Representative sample shown in the layout editor whenever the live list is empty (mirrors the Effects list).
    // One generic "Player" name per script on purpose (sample names, not UI text — so not localized), so the user can
    // check every bar-label font path without such players in range: Latin, Thai (framework ThaiTmpTwin + its dark
    // edge), Japanese and Hangul (CJK stays on the legacy Text). Exactly ThreatSlots rows, sorted DESC like the real list.
    private static readonly TargetInfoTracker.ThreatEntry[] ExampleThreat =
    {
        new(1, "Vanguard",   4800, 0.48f, false),
        new(2, "ผู้เล่น",      2200, 0.22f, false),
        new(3, "You",        1500, 0.15f, true),
        new(4, "プレイヤー", 1000, 0.10f, false),
        new(5, "플레이어",    500, 0.05f, false),
    };

    private IReadOnlyList<TargetInfoTracker.ThreatEntry> ThreatDisplay()
    {
        int f = Time.frameCount;
        if (f == _threatDispFrame) return _threatDisp;
        _threatDispFrame = f;
        _threatDisp.Clear();
        _threatTotal = 0;

        // Layout edit with no target → the sample straight away (no live read). With a target whose live list is
        // empty (no hate table yet) the sample is filled at the end instead, so there is always something to place.
        if (ShowExample) return FillThreatExample();

        _ = Cur;   // poll _targetInfo this frame so LastTargetEntity is fresh before the threat read
        if (_targetInfo.TryGetThreatList(out var full) && full.Count > 0)
        {
            _threatTotal = full.Count;
            // Top-aggro mode (0): show ONLY the single highest-threat holder (list is already sorted DESC by HateVal,
            // so full[0] is the top). No appended-local row — the local player still gets the pale-gold "you" ring if they
            // happen to be full[0] (the "you" ring keys off IsLocal, not position).
            if (_threatMode == 0)
            {
                _threatDisp.Add(full[0]);
                _threatRank[0] = 1;
                return _threatDisp;
            }

            // Aggro-list mode (1, default): Top-N + the local player's appended row when they fall outside the Top-N.
            int n = Math.Min(ThreatTopN, full.Count);
            bool localShown = false;
            for (int i = 0; i < n; i++)
            {
                _threatDisp.Add(full[i]);
                _threatRank[i] = i + 1;
                if (full[i].IsLocal) localShown = true;
            }
            // Local player not in the Top-N but present further down → append their own row so standing is visible.
            if (!localShown)
                for (int i = n; i < full.Count; i++)
                    if (full[i].IsLocal) { _threatRank[_threatDisp.Count] = i + 1; _threatDisp.Add(full[i]); break; }
        }
        if (_threatDisp.Count == 0 && _services.Windows.IsLayoutEditing) return FillThreatExample();
        return _threatDisp;
    }

    // The layout-edit sample: all ThreatSlots rows in both display modes. Static data, no allocation.
    private IReadOnlyList<TargetInfoTracker.ThreatEntry> FillThreatExample()
    {
        _threatDisp.Clear();
        _threatDisp.AddRange(ExampleThreat);
        for (int i = 0; i < ExampleThreat.Length; i++) _threatRank[i] = i + 1;
        _threatTotal = ExampleThreat.Length;
        return _threatDisp;
    }

    private bool ThreatRowVisible(int idx) => idx < ThreatDisplay().Count;

    private int ThreatTotal() { ThreatDisplay(); return _threatTotal; }

    private int ThreatRank(int idx) => idx < ThreatDisplay().Count ? _threatRank[idx] : 0;

    private bool ThreatIsLocal(int idx)
    {
        var l = ThreatDisplay();
        return idx < l.Count && l[idx].IsLocal;
    }

    // Name inside the bar (left). Your own row is marked by the pale-gold ring (Crest redesign), not a text prefix.
    private string ThreatName(int idx)
    {
        var l = ThreatDisplay();
        return idx < l.Count ? l[idx].Name : "";
    }

    // Share % (right), cached per slot: rebuilt only when the rounded value changes.
    private readonly int[] _threatPctVal = { -1, -1, -1, -1, -1 };
    private readonly string[] _threatPctStr = { "", "", "", "", "" };
    private string ThreatPct(int idx)
    {
        var l = ThreatDisplay();
        if (idx >= l.Count) return "";
        int p = (int)Math.Round(l[idx].Pct * 100f);
        if (p != _threatPctVal[idx]) { _threatPctVal[idx] = p; _threatPctStr[idx] = p + "%"; }
        return _threatPctStr[idx];
    }

    private float ThreatFraction(int idx)
    {
        var l = ThreatDisplay();
        if (idx >= l.Count) return 0f;
        return Clamp01(l[idx].Pct);
    }

    // "1".."99" for the medallions, built once each.
    private static readonly string?[] RankStrings = new string?[100];
    private static string RankText(int r) => r <= 0 ? "" : r < 100 ? (RankStrings[r] ??= r.ToString()) : r.ToString();
}
