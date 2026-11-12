using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Effect-picker feedback (house rule 13 — every click answers where you clicked) and the footer (StatInspector
/// Plugin.Settings.Feedback.cs port). A row click flashes that group green with bold "Added" / "Removed" for
/// <see cref="SelFlashDurS"/> — keyed by the group's member array (its identity; the filter re-uses the same arrays) +
/// time, so a scroll, a re-filter or a re-bind keeps the flash on the right row — and puts "✓ Added “x” · N ticked" in
/// the footer for <see cref="SelFooterNoteDurS"/> (otherwise "N ticked"). The footer's Caution "Untick all" pill
/// (recessed while nothing is ticked) swaps the footer for an inline confirm: bold "Untick all N debuffs?" + a muted
/// consequence line (worded per mode) + [Untick all] [Cancel]; unanswered, it falls back after
/// <see cref="SelConfirmDurS"/>. The confirm is taller than the footer: the flexible list above gives up the difference,
/// so the window never changes height (Plugin.SelectWindow.cs sizing note).
/// </summary>
public sealed partial class Plugin
{
    private const float SelFlashDurS      = 1.0f;
    private const float SelFooterNoteDurS = 3.0f;
    private const float SelConfirmDurS    = 4.0f;
    // Pill label boxes = the longest label in any shipped locale (bold 13; id "Hapus centang" ≈ 92, fil "Kanselahin"
    // ≈ 69, ja "キャンセル" ≈ 65) + headroom, so a language switch never resizes a pill (house rule 10).
    private const float UntickPillLabelW = 96f;
    private const float SelCancelLabelW  = 72f;
    // Confirm block: bold question (one line, pinned 18) + consequence (≤ 2 plain lines, pinned 36: id / fil / ja run
    // past one line in 376 px) + the 28 px pill row, 4 px apart = 90.
    private const float ConfirmQuestionH    = 18f;
    private const float ConfirmConsequenceH = 36f;

    private int[]? _selFlashMembers;
    private float  _selFlashAt;
    private bool   _selFlashAdded;
    private string _selFooterNote = string.Empty;   // composed once per click (not per refresh)
    private float  _selFooterNoteAt = float.NegativeInfinity;
    private float  _untickConfirmAt = -1f;          // ≥ 0 while the inline "Untick all" confirm is showing

    private bool UntickConfirming => _untickConfirmAt >= 0f;

    private readonly FmtCache _selFooterFmt  = new();
    private readonly FmtCache _selConfirmFmt = new();

    // ── footer ────────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildSelectFooter() => new ConditionalElement(() => UntickConfirming,
        new ColumnElement(new HudElement[]
        {
            new TextElement(UntickQuestion, Color: () => TextColor(), NoWrap: true, FontSize: 13)
                { Bold = true, Ellipsize = true, Height = ConfirmQuestionH },
            new TextElement(UntickConsequence, Color: () => MutedText()) { MaxLines = 2, Height = ConfirmConsequenceH },
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f),   // flexible: the pills sit at the right edge
                CautionButton(() => _loc.T("tl.sel.untickAll"), ConfirmUntickAll, UntickPillLabelW),
                NeutralButton(() => _loc.T("tl.sel.cancel"), CancelUntickAll, SelCancelLabelW),
            }, Gap: 8f),
        }, Gap: 4f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(SelFooterText, Color: () => SelFooterNoteShowing() ? SuccessColor() : MutedText(),
                NoWrap: true) { Ellipsize = true }, Weight: 1f),
            CautionButton(() => _loc.T("tl.sel.untickAll"), BeginUntickAll, UntickPillLabelW,
                enabled: () => SelActiveTicked > 0),
        }, Gap: 8f))
    { Prebuild = true, PrebuildName = "tl.select.confirm" };

    private bool SelFooterNoteShowing() => _services.Framework.TimeNow - _selFooterNoteAt < SelFooterNoteDurS;

    private string SelFooterText() => SelFooterNoteShowing()
        ? _selFooterNote
        : _selFooterFmt.Get(_loc.T("tl.sel.footer"), SelActiveTicked);

    private string UntickQuestion() => _selConfirmFmt.Get(
        _loc.T(_activeTab == 0 ? "tl.sel.confirm.debuffs" : "tl.sel.confirm.buffs"), SelActiveTicked);

    // What the HUD does once nothing is ticked depends on the mode: "Only the ticked ones" → it shows none of this
    // kind; "All except ticked" → nothing is hidden any more.
    private string UntickConsequence() => _loc.T(SelExcludeMode
        ? (_activeTab == 0 ? "tl.sel.consequence.exceptDebuffs" : "tl.sel.consequence.exceptBuffs")
        : (_activeTab == 0 ? "tl.sel.consequence.onlyDebuffs"   : "tl.sel.consequence.onlyBuffs"));

    // ── row flash ─────────────────────────────────────────────────────────────────────────────────────────────────
    private bool SlotFlashing(int slot)
    {
        var m = _selFlashMembers;
        return m != null && ReferenceEquals(m, SlotMembers(slot)) && _services.Framework.TimeNow - _selFlashAt < SelFlashDurS;
    }

    private string SelFlashLabel() => _loc.T(_selFlashAdded ? "tl.sel.added" : "tl.sel.removed");

    // The green word sits on the green-washed tile; keep it readable on the Light theme too.
    private ColorRgba SelFlashTextColor()
        => EnsureContrast(SuccessColor(), Over(SuccessColor() with { A = FlashWashAlpha }, TileColor()), SolidMinContrast);

    // ── events ────────────────────────────────────────────────────────────────────────────────────────────────────
    private void OnEffectToggled(int[] members, string name, bool added)
    {
        var now = _services.Framework.TimeNow;
        _selFlashMembers = members;
        _selFlashAt = now;
        _selFlashAdded = added;
        _selFooterNote = _loc.TFormat(added ? "tl.sel.feedback.added" : "tl.sel.feedback.removed", name, SelActiveTicked);
        _selFooterNoteAt = now;
        _untickConfirmAt = -1f;   // any other action dismisses a pending confirm
    }

    private void BeginUntickAll()
    {
        if (SelActiveTicked == 0) return;
        _untickConfirmAt = _services.Framework.TimeNow;
    }

    private void CancelUntickAll() => _untickConfirmAt = -1f;

    // Clears the ACTIVE tab's whole selection (every id, so a partially ticked group is cleared too) and persists it
    // like a row toggle does. Re-applying the filter re-sorts the list (nothing floats as ticked any more).
    private void ConfirmUntickAll()
    {
        _untickConfirmAt = -1f;
        bool debuffs = _activeTab == 0;
        if (debuffs) _selection.ClearDebuffs();
        else         _selection.ClearBuffs();
        _selection.Save(_selCfg);
        if (debuffs) ApplyDebuffTabFilter(_dtFilter);   // also bumps _selVersion → counts + row caches recompute
        else         ApplyBuffTabFilter(_btFilter);
        _selFlashMembers = null;
        _selFooterNote = _loc.T(debuffs ? "tl.sel.feedback.clearedDebuffs" : "tl.sel.feedback.clearedBuffs");
        _selFooterNoteAt = _services.Framework.TimeNow;
    }

    // Per-frame (TickSettingsUi): an unanswered confirm falls back to the normal footer. One compare when idle.
    private void AdvanceUntickConfirm()
    {
        if (UntickConfirming && _services.Framework.TimeNow - _untickConfirmAt > SelConfirmDurS) _untickConfirmAt = -1f;
    }
}
