using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Standalone NEARBY MONSTERS overlay — a borderless auto-showing HUD window listing the nearest alive monsters,
// each as a row: [Modern HP bar — name inside-left, HP% inside-right] [distance]. Bosses/elites sort first, then by distance ascending;
// capped at NearbySlots rows and hidden beyond ~60 m (see NearbyMonsterTracker). Mirrors the boss-timer window:
// width-resizable with a locked height, a debounced row-size slider, layout-edit example rows so it can be placed
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

    private static readonly ColorRgba NearbyHpFill     = new(0.40f, 0.78f, 0.42f, 1f);  // green HP bar

    private void RegisterNearbyWindow()
    {
        // Persisted toggle drives both initial visibility and the ShouldRender gate (default ON — user asked for it).
        _nearbyOn = _cfg.Get<bool>("nearby_on", true);
        // Row-size multiplier baked into the element sizes + the locked height + the width band below; a live change
        // re-registers the window (SetNearbyScale → RebuildNearbyWindow). Clamped to the slider band.
        _nearbyScale = System.Math.Clamp(_cfg.Get<float>("nearby_scale", 1f), 1f, 2.5f);
        // Show/hide the title row (default ON). Live via the title's ConditionalElement (no rebuild).
        _showNearbyTitle = _cfg.Get<bool>("show_nearby_title", true);

        float sc = _nearbyScale;
        float NearbyW = 320f * sc;   // name + HP bar + distance
        // Title + NearbySlots rows + gaps + padding, scaled by sc so the locked height grows with the rows. The window
        // is WIDTH-resizable (drag wider to fit longer monster names); Resizable FIXES the height, so we compute an
        // explicit height and LOCK it below (MinHeight==MaxHeight).
        // BaseStride is the per-row vertical budget. Each row's actual content height = barH (the Modern HP bar height,
        // which also sets the row's line box — see BuildNearbyWindowRoot), so the stride must track barH, NOT be trimmed
        // below it: the earlier 21→19 trim shrank the budget below a comfortable row and squished the content. barH settled
        // at the midpoint 20f (18 read squished, 22 read too tall) and the stride kept in lock-step at barH@1× + colGap@1×
        // (20 + 1 = 21) — the row height goes INTO the row, while colGap stays 1f so the inter-row GAP stays tight. MUST stay
        // in lock-step with barH + colGap so the locked height exactly fits NearbySlots rows + title + padding.
        const float TitleReserve = 24f, Pad = 16f, BaseStride = 21f;
        float nearbyH = (TitleReserve + Pad + NearbySlots * BaseStride) * sc;   // ≈ 208 at 1× (fixed, locked height)
        float minW    = 240f * sc;   // real drag floor, scaled with the rows
        float maxW    = 700f * sc;   // generous ceiling, scaled with the rows

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
                // Width-resizable so longer names fit; height LOCKED via MinHeight==MaxHeight so only width drags.
                Resizable = true, MinWidth = minW, MaxWidth = maxW, MinHeight = nearbyH, MaxHeight = nearbyH,
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

    // Re-apply a new _nearbyScale by rebuilding the window Root at the new scale (element sizes + the locked height +
    // the width band are baked at build time). Framework-sanctioned Remove()+Register() (mirrors RebuildBossTimerWindow):
    // preserve ONLY the rect — NOT IsShown (at a rebuild moment with nothing nearby it reads false and would strand
    // the window off); RegisterNearbyWindow's SetVisible(_nearbyOn) + the ShouldRender gate handle visibility.
    private void RebuildNearbyWindow()
    {
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

    // Title + a fixed pool of NearbySlots rows. Each row = [Modern HP bar — name inside-left, HP% inside-right] [distance].
    // Rows collapse to zero height when they have no live entry (ConditionalElement on NearbyRowVisible). Row dimensions scale together
    // by _nearbyScale; the TITLE is kept at a fixed size (excluded from scaling) so only the rows grow.
    private HudElement BuildNearbyWindowRoot()
    {
        float sc       = _nearbyScale;
        int   titlePx  = 14;   // fixed — title glyph does NOT scale with _nearbyScale (only the rows do)
        float barH     = 20f * sc;   // row content height (Modern bar + inside-label line box) — midpoint of 18 (squished) and 22 (too tall)
        int   barFont  = (int)System.Math.Round(12 * sc);   // the bar's LabelFontSize — drives BOTH inside labels (name + HP%) and the distance text
        float distCellW = 44f * sc;
        float rowGap   = 6f * sc;   // horizontal gap BETWEEN cells within a row (name | bar | distance)
        // Vertical gap BETWEEN rows. Kept tight at 1f — the earlier squish was from shrinking the row CONTENT (barH), not
        // the gap, so barH + BaseStride carry the row-size tuning and this stays at 1. Kept in lock-step with BaseStride in
        // RegisterNearbyWindow (BaseStride = barH@1× + this) so the locked height fits exactly.
        float colGap   = 1f * sc;

        var rows = new HudElement[NearbySlots + 1];
        // Wrapped so the "Show nearby list title" toggle collapses the header live (rows shift up; window keeps its size).
        rows[0] = new ConditionalElement(() => _showNearbyTitle,
            new TextElement(() => _loc.T("tl.window.nearby"), Color: MutedColor, Emphasis: true, FontSize: titlePx));
        for (int s = 0; s < NearbySlots; s++)
        {
            int idx = s;
            var row = new RowElement(new HudElement[]
            {
                // The HP bar fills the row width (Weight:1). The monster NAME is the bar's inside-LEFT primary label
                // and the HP% the inside-RIGHT SecondaryLabel (Modern dual-overlay). The framework's Modern label uses
                // HorizontalWrapMode.Overflow and is NOT masked to the track, so a long name would SPILL past the bar —
                // NearbyName() pre-truncates to a width-aware budget and appends an ellipsis so it can't overflow. The
                // name's old rank tint is dropped: the Modern inside label has no per-row colour hook (LabelColor is
                // honoured only by the Default bar path, and is a static value, not a Func of the slot's live occupant).
                new CellElement(
                    new BarElement(() => NearbyFraction(idx), NearbyHpFill, () => NearbyName(idx))
                    { Style = BarStyle.Modern, Height = barH, FillWidth = true, LabelFontSize = barFont,
                      LabelInside = true, SecondaryLabel = () => NearbyHpPct(idx) },
                    Weight: 1f),
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
