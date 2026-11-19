using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Standalone "List" buff/debuff STYLE window — an alternative to the classic tile grid inside the Target HUD.
// One row per active effect: [mini icon] [ time-bar: <name> ....... <time> ]. The bar's FILL is the remaining-time
// fraction; the name sits inside-left (ellipsised) and the compact remaining time inside-right, exactly like the
// Target HUD's HP bar (main label + SecondaryLabel, both LabelInside). Mirrors the threat window (Plugin.ThreatWindow.cs):
// a borderless auto-showing HUD overlay gated by ShouldRender, with layout-edit example rows so it can be sized and
// placed without a live target. Row getters + the height-driven row count live in Plugin.BuffList.cs.
//
// Height-resizable: MORE height = MORE visible rows (BuffListVisibleRows() floors the drag-set height by the row
// stride). Width-resizable too so long names have room. The two styles are mutually exclusive — this window only
// renders while buff_style == 1 (List); in Classic mode it stays hidden and the Target HUD shows its tiles.
public sealed partial class Plugin
{
    private IWindowControl _buffListWindow = null!;   // registered in the ctor; auto-shows on target while in List style

    // Config-backed: show the "Target Effects" title row (List mode only; default ON). Read live each frame by the
    // title's ConditionalElement, so the settings toggle hides/shows the header with no window rebuild.
    private bool _showEffectTitle = true;

    private void RegisterBuffListWindow()
    {
        // Persisted style drives both the window's initial visibility and its ShouldRender gate (1 = List default).
        _buffStyle = _cfg.Get<int>("buff_style", 1);
        // Row-size multiplier (List only). Baked into the element sizes below, so a live change re-registers the
        // window (SetListScale → RebuildBuffListWindow). Clamped to the slider band so a stray config can't break layout.
        _listScale = System.Math.Clamp(_cfg.Get<float>("list_scale", 1f), 1f, 2.5f);
        // Show/hide the title row (default ON). Live via the title's ConditionalElement (no rebuild).
        _showEffectTitle = _cfg.Get<bool>("show_effect_title", true);

        const float ListW   = 300f;           // 1× min width — enough for a mini icon + a name + a compact time
        const int   MinRows = 3;              // shortest useful list
        const int   MaxRows = BuffListSlots;  // 16 — the pool ceiling

        // Height band = chrome padding + the (unscaled) Crest caption block + 3..16 rows at the SCALED stride, so the
        // resize cap grows WITH the rows. The window stays user-resizable within this band (never forced);
        // BuffListVisibleRows floors the dragged height by the same stride. Width scales so the name budget fits.
        float minH = BuffListFixedH + MinRows * BuffRowStride - 3f * _listScale;
        float maxH = BuffListFixedH + MaxRows * BuffRowStride - 3f * _listScale;
        // Default height = the user-approved layout (239 at 1×, 2026-11-19), its row part scaled with the rows.
        float defH = System.Math.Clamp(BuffListFixedH + (239f - BuffListFixedH) * _listScale, minH, maxH);
        float winW = ListW * _listScale;

        // Default position = the user-approved layout (2560x1440: x=360, y=12, w=300), Top-anchored: X is the offset
        // from the horizontally centred spot (360 − (2560 − 300)/2 = −770), so it keeps its place beside the centred
        // boss bar at any resolution. A saved (user-moved) rect always wins over this.
        float x = -770f;
        float y = 12f;

        _buffListWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.bufflist",
                Title:       _loc.T("tl.window.bufflist"),
                DefaultRect: new WindowRect(x, y, winW, defH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false,
                Anchor = WindowAnchor.Top,
                // Height-resizable (row count follows height); width-resizable for name room. Height clamped to the
                // 3..16-row band so it can neither hide the title nor exceed the fixed slot pool.
                Resizable = true, MinWidth = winW, MinHeight = minH, MaxHeight = maxH,
                // Auto-show: mirrors the Target HUD gate (in-world, no menu/loading/blocking overlay, live target),
                // plus the List style selected. With no effects the window stays mounted but draws nothing (its plate hugs
                // the rows). Layout-edit mode overrides so it can be placed against its example rows.
                ShouldRender = () => _services.Windows.IsLayoutEditing
                                  || (_services.ClientState.Phase == GamePhase.World
                                      && (_services.ClientState.UiState & (GameUIState.Loading | GameUIState.Blocking | GameUIState.AnyMenu)) == 0
                                      && _buffStyle == 1
                                      && _targetInfo.Current.HasTarget),
                // Framework pre-build (fw 2.30.0): this window is Visible but held hidden by ShouldRender until a
                // target with effects appears, and its first DRAWN apply built every row (~74 ms). The framework now
                // fills it off-screen once in the world; with no target every row Conditional is false, so the rows
                // carry ConditionalElement.Prebuild (below) to get built in that fill too. No PrebuildSignature: the
                // row pool is fixed (16 slots), so effects arriving later only re-bind built rows. All row getters
                // are guarded for an empty list (no target).
                Prebuild = true,
            },
            Root:    BuildBuffListWindowRoot(),
            OnClose: () => { }));
        _windows.Add(_buffListWindow);   // Dispose already loops _windows and Remove()s each

        // Initial visibility from the persisted style (the settings dropdown flips it live via SetVisible).
        _buffListWindow.SetVisible(_buffStyle == 1);
    }

    // Re-apply a new _listScale: the row element sizes are baked at build time, so a plain slider can't grow them
    // mid-session — the window Root must be rebuilt. The framework-sanctioned Remove()+Register() pattern (mirrors
    // CombatMeter's RebuildSkillBreakdownWindow): capture the current rect, drop the stale dispose-list entry,
    // Remove() the old window, then RegisterBuffListWindow() re-registers into _buffListWindow + _windows at the new
    // scale, and we restore the captured rect so position/size are preserved.
    //
    // We deliberately do NOT capture and restore IsShown. IsShown is "visible AND currently mounted", so at a rebuild
    // moment with no live target it reads false — restoring that would SetVisible(false) and leave the window OFF even
    // after a target appears. This window's session-visibility is purely the List style, which RegisterBuffListWindow
    // already sets (SetVisible(_buffStyle == 1)); the ShouldRender gate re-shows it whenever a target is live.
    private void RebuildBuffListWindow()
    {
        var rect = _buffListWindow.Rect;
        _windows.Remove(_buffListWindow);   // drop the old control so Dispose doesn't Remove() it twice
        _buffListWindow.Remove();
        RegisterBuffListWindow();           // reassigns _buffListWindow + re-adds to _windows AND SetVisible(_buffStyle==1)
        if (rect.Width > 0f) _buffListWindow.SetRect(rect);
    }

    // Crest look (Plugin.Crest.cs): caption "TARGET EFFECTS" + "2 debuffs · 2 buffs" + hairline, then the empty-state
    // line OR a fixed pool of BuffListSlots rows: [20 px icon, orange (debuff) / green (buff) ring] [bar: debuff /
    // buff gradient — two variants, a gradient is fixed at build — name left ("★ " = yours), time right; a permanent
    // effect = full bar at ~55 % alpha + "—"]. Debuffs first, then buffs (ListEffects). Rows collapse when empty or
    // beyond the dragged height. Row sizes scale with _listScale (baked; SetListScale rebuilds); the caption is fixed.
    private HudElement BuildBuffListWindowRoot()
    {
        float sc     = _listScale;
        int   iconPx = (int)System.Math.Round(20 * sc);
        float barH   = 20f * sc;
        int   fontPx = (int)System.Math.Round(12 * sc);

        var rows = new HudElement[BuffListSlots + 1];
        rows[0] = CrestCaption("tl.cap.effects", () => BuffListHint(), () => _showEffectTitle);
        for (int s = 0; s < BuffListSlots; s++)
        {
            int idx = s;
            var debuffBar = CrestBar(() => BuffListFraction(idx), GradDebuff, () => BuffListName(idx), () => BuffListTime(idx), barH, fontPx, sc, () => BuffListTint(idx));
            var buffBar   = CrestBar(() => BuffListFraction(idx), GradBuff,   () => BuffListName(idx), () => BuffListTime(idx), barH, fontPx, sc, () => BuffListTint(idx));
            var row = new RowElement(new HudElement[]
            {
                new CellElement(CrestIcon(() => GetBuffListIcon(idx), () => _buffListUv[idx], iconPx,
                    () => BuffListIsDebuff(idx) ? CrestDebuffRing : CrestBuffRing), Width: iconPx),
                new CellElement(CrestBarRings(BarVariants(barH, (debuffBar, () => BuffListIsDebuff(idx)), (buffBar, () => !BuffListIsDebuff(idx))), sc),
                    Weight: 1f),
            }, Gap: 6f * sc);
            // Prebuild: built in the background even while no row is live (the window pre-build's fill has no target).
            rows[s + 1] = new ConditionalElement(() => BuffListRowVisible(idx), row) { Prebuild = true, PrebuildName = "tl.bufflist.row" };
        }
        // Content-hugging plate; with no effects nothing draws at all (the window keeps its rect).
        return CrestHugFrame(new ColumnElement(rows, Gap: 3f * sc), OvEffects, () => _showEffectTitle, () => BuffListShownRows(),
            BuffListSlots, barH, sc);
    }

    // Window height that isn't rows: chrome padding + the plate padding + the caption block.
    private const float BuffListFixedH = 2f * ChromePad + 2f * PlatePadY + CaptionReserve;
    private int BuffListShownRows() => System.Math.Min(BuffListVisibleRows(), ListEffects().Count);
}
