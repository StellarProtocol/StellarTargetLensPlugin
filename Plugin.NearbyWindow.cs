using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Standalone NEARBY MONSTERS overlay — a borderless auto-showing HUD window listing the nearest alive monsters,
// each as a row: [Modern HP bar — name inside-left, HP% inside-right] [hard-lock padlock slot] [distance]. Bosses/elites sort first, then by distance ascending;
// capped at NearbySlots rows and hidden beyond ~60 m (see NearbyMonsterTracker). Mirrors the boss-timer window:
// width- AND (now) height-resizable, a debounced row-size slider, layout-edit example rows so it can be placed
// without a live encounter, and gated by ShouldRender on the toggle + a non-empty list.
//
// Data: NearbyMonsterTracker.Current (walks ZEntityMgr.MonsterDict ∪ BossDict each frame). Row getters + the
// frame-gated display list live in Plugin.NearbyList.cs.
public sealed partial class Plugin
{
    private IWindowControl _nearbyWindow = null!;   // registered in the ctor; auto-shows when the nearby list is non-empty

    // Row-size multiplier. Baked into the element sizes + the locked height + the resize width band at build time, so
    // a live change re-registers the window (SetNearbyScale → RebuildNearbyWindow, debounced). Default 1.0 = unchanged.
    private float _nearbyScale          = 1f;
    private long  _nearbyScaleDirtyAtMs = -1;   // Environment.TickCount64 of the last slider change; -1 = idle

    // Config-backed: show the "Nearby Monsters" title row (default ON). Read live each frame by the title's
    // ConditionalElement, so the settings toggle hides/shows the header with no window rebuild (rows shift up).
    private bool  _showNearbyTitle      = true;

    private const int NearbySlots = 8;   // fixed row pool (top-N cap in NearbyMonsterTracker)

    private static readonly ColorRgba NearbyLockGold  = CrestHex(0xf3dca0);       // hard-lock padlock tint (white glyph), Crest pale gold
    private static readonly ColorRgba NearbyLockClear = new(1f, 1f, 1f, 0f);       // padlock slot when not locked: fully transparent
    private static readonly ColorRgba NearbyTargetWash = new(80f / 255f, 160f / 255f, 1f, 0.25f);   // current target: pale blue over the empty part
    private const float NearbyDistW = 36f;   // distance cell width at 1x

    // Fixed BOSS/ELITE pill slot width for a row size (the pre-drawn pill for the nearest Crest step).
    private static float NearbyPillW(float sc) => System.MathF.Round(40f * CrestSteps[CrestStep(sc)]);

    private void RegisterNearbyWindow()
    {
        // Persisted toggle drives both initial visibility and the ShouldRender gate (default ON — user asked for it).
        _nearbyOn = _cfg.Get<bool>("nearby_on", true);
        // Row-size multiplier baked into the element sizes + the locked height + the width band below; a live change
        // re-registers the window (SetNearbyScale → RebuildNearbyWindow). Clamped to the slider band.
        _nearbyScale = System.Math.Clamp(_cfg.Get<float>("nearby_scale", 1f), 1f, 2.5f);
        // Show/hide the title row (default ON). Live via the title's ConditionalElement (no rebuild).
        _showNearbyTitle = _cfg.Get<bool>("show_nearby_title", true);
        // Cap the list by the player's per-class lockable range (LockCancelRange) instead of the fixed ~60 m (default OFF).
        // Pushed into the tracker, which reads LockableRangeOnly each poll — the settings chip updates it live (no rebuild).
        // The config key keeps its old "targetable" name so saved settings carry over.
        _nearbyLockableOnly = _cfg.Get<bool>("nearby_targetable_only", false);
        _nearby.LockableRangeOnly = _nearbyLockableOnly;

        float sc = _nearbyScale;
        float NearbyW = 320f * sc;   // pill + name/HP bar + padlock + distance
        // Rows are Bare selectables (no host padding), so the stride is the Crest bar (20) + the row gap (3). The default
        // height = chrome padding + the (unscaled) caption block + all NearbySlots rows; the drag band runs from ~2 rows
        // to double that. The row pool is FIXED (dragging taller adds empty plate below; shorter clips the bottom rows).
        float rowsH   = (NearbySlots * 23f - 3f) * sc;
        float nearbyH = 2f * ChromePad + CaptionReserve + rowsH;   // ~235 at 1x (DEFAULT open height = all 8 rows)
        float minW    = 240f * sc;   // real drag floor, scaled with the rows
        float maxW    = 700f * sc;   // generous ceiling, scaled with the rows
        float minH    = 2f * ChromePad + CaptionReserve + (2 * 23f - 3f) * sc;
        float maxH    = nearbyH * 2f;

        // Default position on the left side, clear of the other overlays' defaults (threat x=1716, boss-timer x=690).
        // Fixed 1440p-calibrated pixel literals (NOT ScreenWidth*frac): "reset all HUD" restores DefaultRect from a
        // path where ScreenWidth is 0, so 0*frac would collapse every window to x=0 — an absolute px restores right.
        float x = 40f;
        float y = 300f;

        _nearbyWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.nearby",
                Title:       _loc.T("tl.window.nearby"),
                DefaultRect: new WindowRect(x, y, NearbyW, nearbyH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false,
                BackgroundColor = CrestPlateFill, BackgroundCornerRadius = CrestPlateRadius,   // Crest plate (Plugin.Crest.cs)
                // Width-resizable so longer names fit; height now ALSO drag-resizable within minH..maxH (user dialing size in live).
                Resizable = true, MinWidth = minW, MaxWidth = maxW, MinHeight = minH, MaxHeight = maxH,
                // Auto-show: in-world, no menu/loading/blocking overlay, the toggle on, AND a non-empty nearby list so
                // it never shows an empty box. Layout-edit mode overrides so the overlay (with example rows) can be placed.
                ShouldRender = () => _services.Windows.IsLayoutEditing
                                  || (_services.ClientState.Phase == GamePhase.World
                                      && (_services.ClientState.UiState & (GameUIState.Loading | GameUIState.Blocking | GameUIState.AnyMenu)) == 0
                                      && _nearbyOn
                                      && NearbyDisplay().Count > 0),
            },
            Root:    BuildNearbyWindowRoot(),
            OnClose: () => { }));
        _windows.Add(_nearbyWindow);   // Dispose already loops _windows and Remove()s each

        // Initial visibility from the persisted toggle (the settings toggle flips it live via SetVisible).
        _nearbyWindow.SetVisible(_nearbyOn);
    }

    // Re-apply a new _nearbyScale by rebuilding the window Root at the new scale (element sizes + the resize bands are
    // baked at build time). Framework-sanctioned Remove()+Register() (mirrors RebuildBossTimerWindow): preserve ONLY the
    // rect — NOT IsShown (at a rebuild moment with nothing nearby it reads false and would strand the window off);
    // RegisterNearbyWindow's SetVisible(_nearbyOn) + the ShouldRender gate handle visibility.
    private void RebuildNearbyWindow()
    {
        // rect carries x/y/width/HEIGHT, and SetRect restores all of it — so a height the user dragged survives the
        // slider-driven rebuild (not regressed to the recomputed DefaultRect height). Guard on Width>0 just skips a
        // never-laid-out rect.
        var rect = _nearbyWindow.Rect;
        _windows.Remove(_nearbyWindow);
        _nearbyWindow.Remove();
        RegisterNearbyWindow();
        if (rect.Width > 0f) _nearbyWindow.SetRect(rect);
    }

    // Nearby size slider handler: update _nearbyScale live (knob + readout track the finger) and (re)arm the debounce;
    // the deferred TickNearbyScaleRebuild does the single persist + rebuild once the drag settles.
    private void SetNearbyScale(float v)
    {
        _nearbyScale = v;
        _nearbyScaleDirtyAtMs = System.Environment.TickCount64;
    }

    // Per-frame (OnTargetHudUpdate) settle check: once the drag has been quiet for ListScaleSettleMs, persist the
    // final value and rebuild the nearby window ONCE at the new scale (mirrors TickBossTimerScaleRebuild).
    private void TickNearbyScaleRebuild()
    {
        if (_nearbyScaleDirtyAtMs < 0) return;
        if (System.Environment.TickCount64 - _nearbyScaleDirtyAtMs < ListScaleSettleMs) return;
        _nearbyScaleDirtyAtMs = -1;
        _cfg.Set<float>("nearby_scale", _nearbyScale);
        _cfg.Save();
        RebuildNearbyWindow();
    }

    // Crest look (Plugin.Crest.cs): caption "NEARBY" + "N monsters" + hairline, then a fixed pool of NearbySlots rows:
    // [40 px rank slot: BOSS / ELITE pill PNG, empty for normal] [bar with the REAL HP fill — boss HP red, elite orange,
    // normal green; your current target blue + a pale blue wash over the empty part + a 1.5 px white ring] [14 px
    // padlock slot, pale gold while the game is locked on] [distance, soft]. The four fills are bar variants (a gradient
    // is fixed at build). Click a row → target it (as before). Row sizes scale with _nearbyScale; the caption is fixed.
    private HudElement BuildNearbyWindowRoot()
    {
        float sc      = _nearbyScale;
        float barH    = 20f * sc;
        int   barFont = (int)System.Math.Round(12 * sc);
        int   lockPx  = NearbyLockPx(sc);       // padlock glyph + its slot width: nearest pre-drawn size to 14 × sc
        var   lockPng = NearbyLockPng(lockPx);  // cached bytes; null (missing resource) → slot stays clear
        int   step    = CrestStep(sc);
        int   pillW   = (int)NearbyPillW(sc), pillH = (int)System.MathF.Round(16f * CrestSteps[step]);
        var   bossPill  = CachedPng($"crest-pill-boss-{step}.png");
        var   elitePill = CachedPng($"crest-pill-elite-{step}.png");

        var rows = new HudElement[NearbySlots + 1];
        rows[0] = CrestCaption("tl.cap.nearby", () => NearbyHint(), () => _showNearbyTitle);
        for (int s = 0; s < NearbySlots; s++)
        {
            int idx = s;
            System.Func<string> name = () => NearbyName(idx), pct = () => NearbyHpPct(idx);
            System.Func<float> hp = () => NearbyFraction(idx);
            var target = CrestBar(hp, GradBlue, name, pct, barH, barFont, sc) with { Trail01 = () => 1f, TrailColor = NearbyTargetWash };
            var bars = BarVariants(barH,
                (target, () => NearbyIsTarget(idx)),
                (CrestBar(hp, GradHp,    name, pct, barH, barFont, sc), () => !NearbyIsTarget(idx) && NearbyIsBoss(idx)),
                (CrestBar(hp, GradElite, name, pct, barH, barFont, sc), () => !NearbyIsTarget(idx) && NearbyIsElite(idx)),
                (CrestBar(hp, GradGreen, name, pct, barH, barFont, sc), () => !NearbyIsTarget(idx) && !NearbyIsBoss(idx) && !NearbyIsElite(idx)));
            var row = new RowElement(new HudElement[]
            {
                // Rank pill (English BOSS / ELITE by design — a symbol). Normal: the slot stays, the pill is cleared.
                new CellElement(new RowElement(new HudElement[]
                {
                    new ImageElement(() => NearbyIsElite(idx) ? elitePill : bossPill, pillW, pillH)
                    { Live = true, Crisp = true, Tint = () => NearbyIsBoss(idx) || NearbyIsElite(idx) ? null : NearbyLockClear },
                }, Gap: 0f, Justify: RowJustify.Center), Width: pillW),
                new CellElement(CrestBarRings(bars, sc, () => NearbyIsTarget(idx), White), Weight: 1f),
                // Hard-lock slot: fixed width, always present — only the tint toggles (no layout jump).
                new CellElement(new RowElement(new HudElement[]
                {
                    new ImageElement(() => lockPng!, lockPx, lockPx)
                    { Tint = () => lockPng != null && NearbyIsLocked(idx) ? NearbyLockGold : NearbyLockClear },
                }, Gap: 0f, Justify: RowJustify.Center), Width: lockPx),
                new CellElement(new TextElement(() => NearbyDist(idx), Color: () => CrestSoft, Align: TextAlign.Right, FontSize: barFont, NoWrap: true)
                    { RichEmoji = true }, Width: NearbyDistW * sc),
            }, Gap: 6f * sc);
            // Left-click → target that monster (main thread; SetGameTarget no-ops on uuid 0). Bare: the click target is
            // exactly the row (no host padding / square wash); the current target is shown by the blue bar + white ring.
            // ⭐ The selectable stays INSIDE the ConditionalElement: a collapsed row's selectable is then an inactive GO
            // (no raycast, no hover) — inverting it would leave an invisible strip that eats world clicks (2026-09-28).
            // ⚠️ No-op on the layout-editor sample rows and during any layout edit: their synthetic uuids name no entity,
            // and the game NREs inside TargetComp.fillSelectData (seen live with uuid=1).
            var selectable = new SelectableElement(row,
                OnClick: () =>
                {
                    if (_nearbyDispIsExample || _services.Windows.IsLayoutEditing) return;
                    _nearby.SetGameTarget(NearbyUuid(idx));
                }) { Bare = true };
            rows[s + 1] = new ConditionalElement(() => NearbyRowVisible(idx), selectable);
        }
        return CrestFrame(new ColumnElement(rows, Gap: 3f * sc));
    }
}
