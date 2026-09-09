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

    // Row-size multiplier. Baked into the element sizes + the locked height + the resize width band at build time, so
    // a live change re-registers the window (SetBossTimerScale → RebuildBossTimerWindow, debounced). Default 1.0 =
    // unchanged. NOTE this is INDEPENDENT of the buff-list's _listScale — the height below no longer uses BuffRowStride
    // (which rides _listScale) but a boss-timer-scaled stride, so resizing the list can't move this window.
    private float          _bossTimerScale        = 1f;
    private long           _bossTimerScaleDirtyAtMs = -1; // Environment.TickCount64 of the last slider change; -1 = idle

    // Config-backed: show the "Boss Skill Timers" title row (default ON). Read live each frame by the title's
    // ConditionalElement, so the settings toggle hides/shows the header with no window rebuild (rows shift up).
    private bool           _showBossTimerTitle    = true;

    private const int BossTimerSlots = 8;              // fixed row pool (the game rarely lists more than a handful)

    // Warm amber like an incoming-danger schedule; a single colour (no debuff/buff split needed here).
    private static readonly ColorRgba BossTimerFill = new(0.95f, 0.62f, 0.22f, 1f);

    private void RegisterBossTimerWindow()
    {
        // Persisted toggle drives both initial visibility and the ShouldRender gate (default ON — user asked for it).
        _bossTimerOn = _cfg.Get<bool>("boss_timer_on", true);
        // Row-size multiplier. Baked into the element sizes + the locked height + the width band below, so a live
        // change re-registers the window (SetBossTimerScale → RebuildBossTimerWindow). Clamped to the slider band.
        _bossTimerScale = System.Math.Clamp(_cfg.Get<float>("bosstimer_scale", 1f), 1f, 2.5f);
        // Show/hide the title row (default ON). Live via the title's ConditionalElement (no rebuild).
        _showBossTimerTitle = _cfg.Get<bool>("show_bosstimer_title", true);

        float sc = _bossTimerScale;
        float TimerW = 300f * sc;   // default width tuned in-game (240–700 band at 1×): icon + name + MM:SS
        // Title + BossTimerSlots rows + gaps + padding, the WHOLE thing scaled by sc so the locked height grows with
        // the rows. The window is WIDTH-resizable (drag wider to fit longer skill names); Resizable disables content-
        // auto-fit and FIXES the height, so we compute an explicit height here and LOCK it below (MinHeight==MaxHeight).
        // Stride is 21f * sc (NOT BuffRowStride, which rides the buff-list's _listScale) so this window is independent.
        const float TitleReserve = 24f, Pad = 16f, BaseStride = 21f;
        float timerH  = (TitleReserve + Pad + BossTimerSlots * BaseStride) * sc;   // ≈ 208 at 1× (fixed, locked height)
        float minW    = 240f * sc;   // real drag floor, scaled with the rows
        float maxW    = 700f * sc;   // generous ceiling, scaled with the rows

        // Default position tuned in-game (user's saved 2560x1440 layout: x=690, y=0). Fixed 1440p-calibrated
        // pixel X (NOT ScreenWidth*frac): "reset all HUD" restores DefaultRect from a path where ScreenWidth is 0,
        // so 0*frac collapsed every window to x=0 — an absolute px restores correctly. y absolute. Own drag overrides.
        float x = 690f;
        float y = 0f;

        _bossTimerWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.bosstimer",
                Title:       _loc.T("tl.window.bosstimer"),
                DefaultRect: new WindowRect(x, y, TimerW, timerH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false,
                // Width-resizable so longer skill names fit; height LOCKED via MinHeight==MaxHeight so only the
                // width drags. MinWidth ≈ the default width (real drag floor); MaxWidth a generous ceiling.
                Resizable = true, MinWidth = minW, MaxWidth = maxW, MinHeight = timerH, MaxHeight = timerH,
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

    // Re-apply a new _bossTimerScale by rebuilding the window Root at the new scale (element sizes + the locked height
    // + the width band are baked at build time). Framework-sanctioned Remove()+Register() (mirrors RebuildBuffListWindow):
    // preserve ONLY the rect — NOT IsShown (at a rebuild moment with no live boss it reads false and would strand the
    // window off); RegisterBossTimerWindow's SetVisible(_bossTimerOn) + the ShouldRender gate handle visibility.
    private void RebuildBossTimerWindow()
    {
        var rect = _bossTimerWindow.Rect;
        _windows.Remove(_bossTimerWindow);
        _bossTimerWindow.Remove();
        RegisterBossTimerWindow();
        if (rect.Width > 0f) _bossTimerWindow.SetRect(rect);
    }

    // Boss-timer size slider handler: update _bossTimerScale live (knob + readout track the finger) and (re)arm the
    // debounce; the deferred TickBossTimerScaleRebuild does the single persist + rebuild once the drag settles.
    private void SetBossTimerScale(float v)
    {
        _bossTimerScale = v;
        _bossTimerScaleDirtyAtMs = System.Environment.TickCount64;
    }

    // Per-frame (OnTargetHudUpdate) settle check: once the drag has been quiet for ListScaleSettleMs, persist the
    // final value and rebuild the boss-timer window ONCE at the new scale (mirrors TickListScaleRebuild).
    private void TickBossTimerScaleRebuild()
    {
        if (_bossTimerScaleDirtyAtMs < 0) return;
        if (System.Environment.TickCount64 - _bossTimerScaleDirtyAtMs < ListScaleSettleMs) return;
        _bossTimerScaleDirtyAtMs = -1;
        _cfg.Set<float>("bosstimer_scale", _bossTimerScale);
        _cfg.Save();
        RebuildBossTimerWindow();
    }

    // Title + a fixed pool of BossTimerSlots rows. Each row = [optional skill icon] [countdown bar]. Rows collapse
    // to zero height when they have no live entry (ConditionalElement on BossTimerRowVisible). Row dimensions (icon,
    // icon cell width, bar height, font, gaps) scale together by _bossTimerScale so the list grows coherently; baked
    // here at build time (SetBossTimerScale rebuilds the window to re-apply a change).
    // The TITLE is intentionally kept at a fixed size (excluded from scaling) so only the rows grow.
    private HudElement BuildBossTimerWindowRoot()
    {
        float sc     = _bossTimerScale;
        int   titlePx = 14;   // fixed — title glyph does NOT scale with _bossTimerScale (only the rows do)
        int   iconPx  = (int)System.Math.Round(18 * sc);
        float cellW   = 22f * sc;
        float barH    = 18f * sc;
        int   fontPx  = (int)System.Math.Round(13 * sc);
        float rowGap  = 6f * sc;
        float colGap  = 3f * sc;

        var rows = new HudElement[BossTimerSlots + 1];
        // Wrapped so the "Show boss timers title" toggle collapses the header live (rows shift up; window keeps its size).
        rows[0] = new ConditionalElement(() => _showBossTimerTitle,
            new TextElement(() => _loc.T("tl.window.bosstimer"), Color: MutedColor, Emphasis: true, FontSize: titlePx));
        for (int s = 0; s < BossTimerSlots; s++)
        {
            int idx = s;
            var bar = new BarElement(() => BossTimerFraction(idx), BossTimerFill, () => BossTimerName(idx))
            {
                Style = BarStyle.Modern, Height = barH, FillWidth = true, LabelFontSize = fontPx,
                LabelInside = true, SecondaryLabel = () => BossTimerTime(idx),
            };
            var row = new RowElement(new HudElement[]
            {
                new CellElement(
                    new GameTextureElement(() => GetBossTimerIcon(idx), iconPx, iconPx, () => _bossTimerUv[idx]),
                    Width: cellW),
                new CellElement(bar, Weight: 1f),
            }, Gap: rowGap);
            rows[s + 1] = new ConditionalElement(() => BossTimerRowVisible(idx), row);
        }
        return new ColumnElement(rows, Gap: colGap) { Padding = 8 };
    }
}
