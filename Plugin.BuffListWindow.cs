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
        // Persisted style drives both the window's initial visibility and its ShouldRender gate (0 = Classic default).
        _buffStyle = _cfg.Get<int>("buff_style", 0);

        const float ListW = 300f;                 // min width — enough for a mini icon + a name + a compact time
        const float TitleReserve = 24f;           // title line + gap (matches BuffList.cs)
        const float Pad          = 16f;           // ColumnElement padding (8*2)
        const int   DefaultRows  = 8;             // out-of-box height ≈ 8 rows
        const int   MinRows      = 3;             // shortest useful list
        const int   MaxRows      = BuffListSlots; // 16 — the pool ceiling
        float minH = TitleReserve + Pad + MinRows     * BuffRowStride;   // ≈ 103
        float maxH = TitleReserve + Pad + MaxRows     * BuffRowStride;   // ≈ 376
        float defH = TitleReserve + Pad + DefaultRows * BuffRowStride;   // ≈ 208

        // Default position tuned in-game (user's saved 2560x1440 layout: x=690, y=73). X is a fraction of
        // ScreenWidth (0.2695*2560≈690) so it holds across resolutions; y absolute. Own saved drag/resize overrides.
        float x = _services.Framework.ScreenWidth * 0.2695f;
        float y = 73f;

        _buffListWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.bufflist",
                Title:       "Target Effects",
                DefaultRect: new WindowRect(x, y, ListW, defH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false,
                // Height-resizable (row count follows height); width-resizable for name room. Height clamped to the
                // 3..16-row band so it can neither hide the title nor exceed the fixed slot pool.
                Resizable = true, MinWidth = ListW, MinHeight = minH, MaxHeight = maxH,
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

    // Title + a fixed pool of BuffListSlots rows. Each row = [mini icon] [ time-bar ]. The bar is split into two
    // mutually-exclusive BarElements (debuff red/orange vs buff green) via a per-row ConditionalElement — BarElement's
    // colour arg is a ColorRgba VALUE, not a Func, so the colour can't vary within one bar. Each row collapses to zero
    // height when it has no live effect OR doesn't fit at the current height (ConditionalElement on BuffListRowVisible).
    private HudElement BuildBuffListWindowRoot()
    {
        var rows = new HudElement[BuffListSlots + 1];
        rows[0] = new TextElement(() => "Target Effects", Color: MutedColor, Emphasis: true, FontSize: 14);
        for (int s = 0; s < BuffListSlots; s++)
        {
            int idx = s;
            var debuffBar = new BarElement(() => BuffListFraction(idx), DebuffAccent, () => BuffListName(idx))
            {
                Style = BarStyle.Modern, Height = 18f, FillWidth = true, LabelFontSize = 13,
                LabelInside = true, SecondaryLabel = () => BuffListTime(idx),
            };
            var buffBar = new BarElement(() => BuffListFraction(idx), BuffAccent, () => BuffListName(idx))
            {
                Style = BarStyle.Modern, Height = 18f, FillWidth = true, LabelFontSize = 13,
                LabelInside = true, SecondaryLabel = () => BuffListTime(idx),
            };
            var row = new RowElement(new HudElement[]
            {
                new CellElement(
                    new GameTextureElement(() => GetBuffListIcon(idx), 18, 18, () => _buffListUv[idx]),
                    Width: 22f),
                new CellElement(
                    new ConditionalElement(() => BuffListIsDebuff(idx), debuffBar, buffBar),
                    Weight: 1f),
            }, Gap: 6f);
            rows[s + 1] = new ConditionalElement(() => BuffListRowVisible(idx), row);
        }
        return new ColumnElement(rows, Gap: 3f) { Padding = 8 };
    }
}
