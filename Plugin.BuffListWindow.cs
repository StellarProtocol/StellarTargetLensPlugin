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

    private void RegisterBuffListWindow()
    {
        // Persisted style drives both the window's initial visibility and its ShouldRender gate (1 = List default).
        _buffStyle = _cfg.Get<int>("buff_style", 1);
        // Row-size multiplier (List only). Baked into the element sizes below, so a live change re-registers the
        // window (SetListScale → RebuildBuffListWindow). Clamped to the slider band so a stray config can't break layout.
        _listScale = System.Math.Clamp(_cfg.Get<float>("list_scale", 1f), 1f, 2.5f);

        const float ListW = 300f;                 // 1× min width — enough for a mini icon + a name + a compact time
        const float TitleReserve = 24f;           // title line + gap (matches BuffList.cs)
        const float Pad          = 16f;           // ColumnElement padding (8*2)
        const float BaseStride   = 21f;           // one row's footprint at 1× (BuffRowStride without the scale)
        const int   MinRows      = 3;             // shortest useful list
        const int   MaxRows      = BuffListSlots; // 16 — the pool ceiling

        // The WHOLE height band scales by _listScale so the resize cap grows WITH the rows — otherwise the 1× max
        // would stop the user dragging tall enough to see many big rows. At 1× ≈ 103..376 (default 376); at 2× ≈
        // 206..752 (default 752). The window stays user-resizable within this band (we never force the dragged size);
        // BuffListVisibleRows floors the (fixed-overhead-subtracted) height by the SCALED stride, so the max still
        // fits the full 16-row pool at any scale. Width scales too (below) so the fixed 22-char name budget still
        // fits the larger font — a bigger row gets a proportionally wider window rather than clipping names.
        float minH = (TitleReserve + Pad + MinRows * BaseStride) * _listScale;
        float maxH = (TitleReserve + Pad + MaxRows * BaseStride) * _listScale;
        float defH = maxH;   // out-of-box height = the full row pool at the current scale (top of the resize band)
        float winW = ListW * _listScale;   // default + min width grow with scale so names keep their room at big fonts

        // Default position tuned in-game (user's saved 2560x1440 layout: x=690, y=73). Fixed 1440p-calibrated
        // pixel X (NOT ScreenWidth*frac): "reset all HUD" restores DefaultRect from a path where ScreenWidth is 0,
        // so 0*frac collapsed every window to x=0 — an absolute px restores correctly. y absolute. Own drag overrides.
        float x = 690f;
        float y = 73f;

        _buffListWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.bufflist",
                Title:       _loc.T("tl.window.bufflist"),
                DefaultRect: new WindowRect(x, y, winW, defH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false,
                // Height-resizable (row count follows height); width-resizable for name room. Height clamped to the
                // 3..16-row band so it can neither hide the title nor exceed the fixed slot pool.
                Resizable = true, MinWidth = winW, MinHeight = minH, MaxHeight = maxH,
                // Auto-show: mirrors the Target HUD gate (in-world, no menu/loading/blocking overlay, live target),
                // plus the List style selected AND a non-empty effect list so it never shows an empty box. Layout-edit
                // mode overrides so the window can be positioned against its example rows without a live target.
                ShouldRender = () => _services.Windows.IsLayoutEditing
                                  || (_services.ClientState.Phase == GamePhase.World
                                      && (_services.ClientState.UiState & (GameUIState.Loading | GameUIState.Blocking | GameUIState.AnyMenu)) == 0
                                      && _buffStyle == 1
                                      && _targetInfo.Current.HasTarget
                                      && CurEffects().Count > 0),
            },
            Root:    BuildBuffListWindowRoot(),
            OnClose: () => { }));
        _windows.Add(_buffListWindow);   // Dispose already loops _windows and Remove()s each

        // Initial visibility from the persisted style (the settings dropdown flips it live via SetVisible).
        _buffListWindow.SetVisible(_buffStyle == 1);
    }

    // Live re-apply of a new _listScale: the row element sizes are baked at build time, so a plain slider can't grow
    // them mid-session — the window Root must be rebuilt. The framework-sanctioned Remove()+Register() pattern
    // (mirrors CombatMeter's RebuildSkillBreakdownWindow): capture the current rect + visibility, drop the stale
    // dispose-list entry, Remove() the old window, then RegisterBuffListWindow() re-registers into _buffListWindow +
    // _windows at the new scale, and we restore the captured rect + visibility so position/size/shown are preserved.
    private void RebuildBuffListWindow()
    {
        var rect     = _buffListWindow.Rect;
        var wasShown = _buffListWindow.IsShown;
        _windows.Remove(_buffListWindow);   // drop the old control so Dispose doesn't Remove() it twice
        _buffListWindow.Remove();
        RegisterBuffListWindow();           // reassigns _buffListWindow + re-adds to _windows
        if (rect.Width > 0f) _buffListWindow.SetRect(rect);
        _buffListWindow.SetVisible(wasShown);
    }

    // Title + a fixed pool of BuffListSlots rows. Each row = [mini icon] [ time-bar ]. The bar is split into two
    // mutually-exclusive BarElements (debuff red/orange vs buff green) via a per-row ConditionalElement — BarElement's
    // colour arg is a ColorRgba VALUE, not a Func, so the colour can't vary within one bar. Each row collapses to zero
    // height when it has no live effect OR doesn't fit at the current height (ConditionalElement on BuffListRowVisible).
    private HudElement BuildBuffListWindowRoot()
    {
        // Row dimensions scale together by _listScale (List style only) so the list grows coherently: icon px, icon
        // cell width, bar height, label font, the horizontal icon↔bar gap, and the per-row vertical gap (which, with
        // the scaled bar height, reproduces the scaled BuffRowStride the height-driven row count is floored by).
        // Baked here at build time — SetListScale rebuilds the window (RebuildBuffListWindow) to re-apply a new scale.
        float sc      = _listScale;
        int   iconPx  = (int)System.Math.Round(18 * sc);
        float cellW   = 22f * sc;
        float barH    = 18f * sc;
        int   fontPx  = (int)System.Math.Round(13 * sc);
        float rowGap  = 6f * sc;
        float colGap  = 3f * sc;

        var rows = new HudElement[BuffListSlots + 1];
        rows[0] = new TextElement(() => _loc.T("tl.window.bufflist"), Color: MutedColor, Emphasis: true, FontSize: 14);
        for (int s = 0; s < BuffListSlots; s++)
        {
            int idx = s;
            var debuffBar = new BarElement(() => BuffListFraction(idx), DebuffAccent, () => BuffListName(idx))
            {
                Style = BarStyle.Modern, Height = barH, FillWidth = true, LabelFontSize = fontPx,
                LabelInside = true, SecondaryLabel = () => BuffListTime(idx),
            };
            var buffBar = new BarElement(() => BuffListFraction(idx), BuffAccent, () => BuffListName(idx))
            {
                Style = BarStyle.Modern, Height = barH, FillWidth = true, LabelFontSize = fontPx,
                LabelInside = true, SecondaryLabel = () => BuffListTime(idx),
            };
            var row = new RowElement(new HudElement[]
            {
                new CellElement(
                    new GameTextureElement(() => GetBuffListIcon(idx), iconPx, iconPx, () => _buffListUv[idx]),
                    Width: cellW),
                new CellElement(
                    new ConditionalElement(() => BuffListIsDebuff(idx), debuffBar, buffBar),
                    Weight: 1f),
            }, Gap: rowGap);
            rows[s + 1] = new ConditionalElement(() => BuffListRowVisible(idx), row);
        }
        return new ColumnElement(rows, Gap: colGap) { Padding = 8 };
    }
}
