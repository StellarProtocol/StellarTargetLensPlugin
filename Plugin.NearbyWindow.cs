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

    private static readonly ColorRgba NearbyHpFill      = new(0.40f, 0.78f, 0.42f, 1f);  // green HP bar
    private static readonly ColorRgba NearbyBossColor   = new(0.93f, 0.23f, 0.20f, 1f);  // red — boss rank fill
    private static readonly ColorRgba NearbyEliteColor  = new(0.95f, 0.55f, 0.15f, 1f);  // dark orange — elite rank fill
    private static readonly ColorRgba NearbyTargetColor = new(0.30f, 0.60f, 0.95f, 1f);  // azure — current-target fill (overrides rank; white name/HP% stay readable)
    private static readonly ColorRgba NearbyLockGold    = new(1.00f, 0.82f, 0.40f, 1f);  // #ffd166 — hard-lock padlock tint (white glyph)
    private static readonly ColorRgba NearbyLockClear   = new(1f, 1f, 1f, 0f);           // padlock slot when not locked: fully transparent

    private void RegisterNearbyWindow()
    {
        // Persisted toggle drives both initial visibility and the ShouldRender gate (default ON — user asked for it).
        _nearbyOn = _cfg.Get<bool>("nearby_on", true);
        // Row-size multiplier baked into the element sizes + the locked height + the width band below; a live change
        // re-registers the window (SetNearbyScale → RebuildNearbyWindow). Clamped to the slider band.
        _nearbyScale = System.Math.Clamp(_cfg.Get<float>("nearby_scale", 1f), 1f, 2.5f);
        // Show/hide the title row (default ON). Live via the title's ConditionalElement (no rebuild).
        _showNearbyTitle = _cfg.Get<bool>("show_nearby_title", true);
        // Cap the list by the player's per-class targetable range instead of the fixed ~60 m (default OFF). Pushed
        // into the tracker, which reads TargetableOnly each poll — the settings toggle updates it live (no rebuild).
        _nearbyTargetableOnly = _cfg.Get<bool>("nearby_targetable_only", false);
        _nearby.TargetableOnly = _nearbyTargetableOnly;

        float sc = _nearbyScale;
        float NearbyW = 320f * sc;   // name + HP bar + distance
        // Title + NearbySlots rows + gaps + padding, scaled by sc so the default height grows with the rows. The window
        // is now BOTH width- AND height-resizable (user asked to dial the size in live "for now"): DefaultRect height stays
        // the all-8-rows height so it opens at today's size, but a height BAND (minH..maxH below) lets it drag taller/shorter.
        // Note the row pool is FIXED at NearbySlots — the window does NOT reflow row count to the dragged height, so dragging
        // taller just adds empty space below and shorter clips the bottom rows (acceptable for this manual-resize knob).
        // BaseStride is the per-row vertical budget. Unlike the threat/boss lists, a Nearby row is wrapped in a
        // SelectableElement (click-to-target), whose framework-fixed host VLG adds 8px of vertical padding around the
        // bar (RectOffset(6,6,4,4) in WindowBuilder.Table.cs). So each row's on-screen box = barH + 8, and the real
        // row-to-row rhythm = box + colGap. colGap is NEGATIVE (-7) in BuildNearbyWindowRoot to cancel that 8px down
        // to a ~1px inter-bar gap, so the rhythm = (barH + 8) + colGap = (20 + 8) + (-7) = 21 at 1× — which is exactly
        // BaseStride. Keep BaseStride == barH@1× + 1 (= 21); it MUST equal that rhythm so the locked height fits
        // NearbySlots rows + title + padding. (At higher sc the fixed 8px/-7 drift ~1px/row, pooled harmlessly as
        // bottom slack in the resizable band.)
        const float TitleReserve = 24f, Pad = 16f, BaseStride = 21f;
        float nearbyH = (TitleReserve + Pad + NearbySlots * BaseStride) * sc;   // ≈ 208 at 1× (DEFAULT open height = all 8 rows)
        float minW    = 240f * sc;   // real drag floor, scaled with the rows
        float maxW    = 700f * sc;   // generous ceiling, scaled with the rows
        // Height band: floor = title + ~2 rows so it can't collapse to nothing (≈ 82 at 1×); ceiling ≈ double the all-8-rows
        // height (≈ 416 at 1×) for generous headroom. Both track the same scaled stride as nearbyH so they grow with the rows.
        float minH    = (TitleReserve + Pad + 2 * BaseStride) * sc;
        float maxH    = (TitleReserve + Pad + NearbySlots * BaseStride) * sc * 2f;

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

    // Title + a fixed pool of NearbySlots rows. Each row = [Modern HP bar — name inside-left, HP% inside-right] [padlock] [distance].
    // Rows collapse to zero height when they have no live entry (ConditionalElement on NearbyRowVisible). Row dimensions scale together
    // by _nearbyScale; the TITLE is kept at a fixed size (excluded from scaling) so only the rows grow.
    private HudElement BuildNearbyWindowRoot()
    {
        float sc       = _nearbyScale;
        int   titlePx  = 14;   // fixed — title glyph does NOT scale with _nearbyScale (only the rows do)
        float barH     = 20f * sc;   // row content height (Modern bar + inside-label line box) — midpoint of 18 (squished) and 22 (too tall)
        int   barFont  = (int)System.Math.Round(12 * sc);   // the bar's LabelFontSize — drives BOTH inside labels (name + HP%) and the distance text
        float distCellW = 44f * sc;
        int   lockPx   = NearbyLockPx(sc);       // padlock glyph + its slot width: nearest pre-drawn size to 14 × sc
        var   lockPng  = NearbyLockPng(lockPx);  // cached bytes; null (missing resource) → slot stays clear
        float rowGap   = 6f * sc;   // horizontal gap BETWEEN cells within a row (name | bar | distance)
        // Vertical gap BETWEEN rows. NEGATIVE on purpose. Each row is wrapped in a SelectableElement (click-to-target),
        // whose framework-fixed host VLG adds RectOffset(6,6,4,4) = 8px of vertical padding (4 top + 4 bottom) around the
        // bar — see WindowBuilder.Table.cs BuildSelectable. THAT 8px, not this gap, was the big dark space between bars
        // (threat/boss rows aren't wrapped in a Selectable, so they pack tight with a small positive gap). There is no
        // plugin-side knob to drop that padding, so we CANCEL it with a negative column spacing: the on-screen gap between
        // two bars = 8 (selectable pad) + colGap, so colGap = -7 leaves ≈1px between bars regardless of monster count.
        // FIXED (not *sc): the 8px padding is a fixed pixel constant, so the cancellation must be fixed too — a scaled
        // colGap would over-cancel and overlap the bars at higher row-size scales. The selectable boxes overlap by ~7px
        // in their (empty) padding zones, but the visible green bars stay 1px apart and each bar sits solely inside its
        // own box, so click-to-target stays unambiguous on the bars. See the BaseStride note in RegisterNearbyWindow.
        float colGap   = -7f;

        var rows = new HudElement[NearbySlots + 1];
        // Wrapped so the "Show nearby list title" toggle collapses the header live (rows shift up; window keeps its size).
        // The title carries NO selectable padding, so the uniform negative colGap (above) would otherwise pull the first
        // bar up INTO the title text. Give the title a matching 4px inset (ColumnElement Padding=4 → 8px vertical, same as
        // the selectable rows' RectOffset top+bottom) so the title↔first-row gap lands at the same ≈1px as row↔row, and
        // the title glyph sits 4px below the window top in lockstep with the rows' 4px bar inset. Collapses cleanly when
        // the "show title" toggle is off (whole wrapper goes inactive → rows shift fully up).
        rows[0] = new ConditionalElement(() => _showNearbyTitle,
            new ColumnElement(new HudElement[]
            {
                new TextElement(() => _loc.T("tl.window.nearby"), Color: MutedColor, Emphasis: true, FontSize: titlePx),
            }, Gap: 0f) { Padding = 4 });
        for (int s = 0; s < NearbySlots; s++)
        {
            int idx = s;
            var row = new RowElement(new HudElement[]
            {
                // The HP bar fills the row width (Weight:1). The monster NAME is the bar's inside-LEFT primary label
                // and the HP% the inside-RIGHT SecondaryLabel (Modern dual-overlay). The framework's Modern label uses
                // HorizontalWrapMode.Overflow and is NOT masked to the track, so a long name would SPILL past the bar —
                // NearbyName() pre-truncates to a width-aware budget and appends an ellipsis so it can't overflow. The
                // rank tint (boss red / elite orange) is carried by the bar FILL, not the text: FillColorFn — a Func the
                // framework calls per row — overrides the static green NearbyHpFill when non-null, so a recycled slot
                // re-colours its fill when its occupant changes rank (null → the static green fallback). The current-TARGET
                // row (NearbyIsTarget) overrides rank with a blue fill so the locked monster is obvious at a glance — this is
                // ADDITIONAL to the SelectableElement's Selected accent tint on the same row (the blue is on the fill, the
                // tint on the row background, so they reinforce rather than clash). The NAME and the HP% SecondaryLabel both
                // stay at the framework default (white); HP amount reads from fill length + the %.
                new CellElement(
                    new BarElement(() => NearbyFraction(idx), NearbyHpFill, () => NearbyName(idx))
                    { Style = BarStyle.Modern, Height = barH, FillWidth = true, LabelFontSize = barFont,
                      LabelInside = true, FillColorFn = () => NearbyIsTarget(idx) ? (ColorRgba?)NearbyTargetColor : NearbyRowColor(idx), SecondaryLabel = () => NearbyHpPct(idx) },
                    Weight: 1f),
                // Hard-lock slot: FIXED width, always present — the padlock only toggles its tint (gold ↔ fully clear),
                // so the bar never changes width when a lock engages/releases (no layout jump). Hard lock = the game's
                // lock-on (ZWorld.LockTargetUuid) from either the lock key or a row click. The Row wrapper keeps the
                // square glyph from being width-stretched by the cell (WindowBuilder-Patterns: cell force-expands width)
                // and centres it; the outer row centres it vertically.
                new CellElement(
                    new RowElement(new HudElement[]
                    {
                        new ImageElement(() => lockPng!, lockPx, lockPx)
                        { Tint = () => lockPng != null && NearbyIsLocked(idx) ? NearbyLockGold : NearbyLockClear },
                    }, Gap: 0f, Justify: RowJustify.Center),
                    Width: lockPx),
                new CellElement(
                    new TextElement(() => NearbyDist(idx), Color: MutedColor, FontSize: barFont),
                    Width: distCellW),
            }, Gap: rowGap);
            // Left-click a row → set the player's real in-game combat target to that monster (OnClick runs on the Unity
            // main thread, so the tracker's ForceSetTarget call is safe direct; SetGameTarget no-ops on uuid 0, i.e. a
            // collapsed/empty row). Selected: the framework's standard 3-state accent tint washes the row that matches
            // the live current target (self-syncing via CurrentTargetUuid — NearbyIsTarget is false for uuid 0).
            //
            // ⭐ SelectableElement stays WRAPPED INSIDE the ConditionalElement (verified 2026-09-28, do NOT invert this):
            // ConditionalElement does NOT mount/unmount — WindowBuilder builds BOTH branches ONCE and CondBinding.Apply()
            // only diff-guarded-SetActive-toggles them, and every WindowInteractionTicker hit-test guards
            // `activeInHierarchy`. So a collapsed row's selectable is an INACTIVE GO — its raycast target is off and its
            // hover is skipped: no per-frame churn AND no dead click-zone. Inverting to selectable(Cond(row)) would keep
            // the selectable permanently ACTIVE with `bg.raycastTarget=true` + a full-width LayoutElement + a ≥8px VLG
            // padding floor over a 0-height child → an invisible full-width strip that eats WORLD clicks. No BringToFront
            // (opens from hidden in its own HUD ZCat) or FrontWindowBlocks (the ticker owns the selectable's hit-testing).
            var selectable = new SelectableElement(row,
                OnClick: () => _nearby.SetGameTarget(NearbyUuid(idx)),
                Selected: () => NearbyIsTarget(idx));
            rows[s + 1] = new ConditionalElement(() => NearbyRowVisible(idx), selectable);
        }
        return new ColumnElement(rows, Gap: colGap) { Padding = 8 };
    }
}
