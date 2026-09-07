using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Standalone BOSS SKILL TIMERS overlay — our replica of the game's native "deadly boss skills" (DBM) countdown
// list. A borderless auto-showing HUD window that renders the active encounter's upcoming boss skills, each as a
// row: [optional skill icon] [countdown bar: <name> ........ MM:SS]. The bar FILL is the remaining-time fraction,
// the name sits inside-left (ellipsised) and MM:SS inside-right (SecondaryLabel) — the same row style as the buff
// list (Plugin.BuffListWindow.cs). Mirrors the threat window: gated by ShouldRender, with layout-edit
// example rows so it can be placed without a live boss.
//
// This shows the whole upcoming-skill schedule for the encounter. The list is a single GLOBAL encounter list
// (NOT keyed per target), so it gates on the list being non-empty, not on having a target.
//
// Data: BossDbmTracker.Current (fed by a Harmony postfix on Panda.ZUi.DBMMgr.onDBMDatacChanged — see DbmPatch.cs).
// Row getters + the OWN icon-UV pool live
// in Plugin.BossTimer.cs.
public sealed partial class Plugin
{
    private IWindowControl _bossTimerWindow = null!;   // registered in the ctor; auto-shows when the DBM list is non-empty
    private bool           _bossTimerOn;               // config-backed: enable the overlay (default ON — user asked for it)

    private const int BossTimerSlots = 8;              // fixed row pool (the game rarely lists more than a handful)

    // Warm amber like an incoming-danger schedule; a single colour (no debuff/buff split needed here).
    private static readonly ColorRgba BossTimerFill = new(0.95f, 0.62f, 0.22f, 1f);

    private void RegisterBossTimerWindow()
    {
        // Persisted toggle drives both initial visibility and the ShouldRender gate (default ON — user asked for it).
        _bossTimerOn = _cfg.Get<bool>("boss_timer_on", true);

        const float TimerW = 260f;   // default width: icon + name + a compact MM:SS
        // Title + BossTimerSlots rows (~21px each) + gaps + padding. The window is WIDTH-resizable (drag wider to
        // fit longer skill names); Resizable disables content-auto-fit and FIXES the height, so we compute an
        // explicit height here and LOCK it below (MinHeight==MaxHeight) — width drags, height cannot.
        const float TitleReserve = 24f, Pad = 16f;
        float timerH = TitleReserve + Pad + BossTimerSlots * BuffRowStride;   // ≈ 208 (the fixed, locked height)

        // The game's native DBM list sits top-LEFT; our other overlays (Target HUD / threat / buff list) live
        // top-RIGHT. Put ours on the LEFT side, mid-upper, so it never overlaps them out of the box. The user's own
        // saved drag/resize overrides this.
        const float x = 40f;
        const float y = 300f;

        _bossTimerWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.bosstimer",
                Title:       "Boss Skill Timers",
                DefaultRect: new WindowRect(x, y, TimerW, timerH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false,
                // Width-resizable so longer skill names fit; height LOCKED via MinHeight==MaxHeight so only the
                // width drags. MinWidth ≈ the default width (real drag floor); MaxWidth a generous ceiling.
                Resizable = true, MinWidth = 240f, MaxWidth = 700f, MinHeight = timerH, MaxHeight = timerH,
                // Auto-show: in-world, no menu/loading/blocking overlay, the toggle on, AND a non-empty DBM list so it
                // never shows an empty box. NOTE: gated on the list, NOT on a target — the DBM list is global/target-
                // independent. Layout-edit mode overrides so the overlay (with example rows) can be positioned.
                ShouldRender = () => _services.Windows.IsLayoutEditing
                                  || (_services.ClientState.Phase == GamePhase.World
                                      && (_services.ClientState.UiState & (GameUIState.Loading | GameUIState.Blocking | GameUIState.AnyMenu)) == 0
                                      && _bossTimerOn
                                      && _bossDbm.Current.Count > 0),
            },
            Root:    BuildBossTimerWindowRoot(),
            OnClose: () => { }));
        _windows.Add(_bossTimerWindow);   // Dispose already loops _windows and Remove()s each

        // Initial visibility from the persisted toggle (the settings toggle flips it live via SetVisible).
        _bossTimerWindow.SetVisible(_bossTimerOn);
    }

    // Title + a fixed pool of BossTimerSlots rows. Each row = [optional skill icon] [countdown bar]. Rows collapse
    // to zero height when they have no live entry (ConditionalElement on BossTimerRowVisible).
    private HudElement BuildBossTimerWindowRoot()
    {
        var rows = new HudElement[BossTimerSlots + 1];
        rows[0] = new TextElement(() => "Boss Skill Timers", Color: MutedColor, Emphasis: true, FontSize: 14);
        for (int s = 0; s < BossTimerSlots; s++)
        {
            int idx = s;
            var bar = new BarElement(() => BossTimerFraction(idx), BossTimerFill, () => BossTimerName(idx))
            {
                Style = BarStyle.Modern, Height = 18f, FillWidth = true, LabelFontSize = 13,
                LabelInside = true, SecondaryLabel = () => BossTimerTime(idx),
            };
            var row = new RowElement(new HudElement[]
            {
                new CellElement(
                    new GameTextureElement(() => GetBossTimerIcon(idx), 18, 18, () => _bossTimerUv[idx]),
                    Width: 22f),
                new CellElement(bar, Weight: 1f),
            }, Gap: 6f);
            rows[s + 1] = new ConditionalElement(() => BossTimerRowVisible(idx), row);
        }
        return new ColumnElement(rows, Gap: 3f) { Padding = 8 };
    }
}
