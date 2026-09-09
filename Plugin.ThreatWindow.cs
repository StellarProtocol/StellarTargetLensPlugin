using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Standalone THREAT / AGGRO window — a separate auto-showing HUD overlay (no longer embedded in the Target HUD).
// Renders the Top-N highest-aggro rows off the current monster's AttrHateList (474), plus the local player's own
// appended row when they fall outside the Top-N (up to ThreatSlots). The row getters + the frame-gated display
// list live in Plugin.TargetHud.Threat.cs; this file owns the window registration + its content root.
//
// Because it's its own window (not the height-locked Target HUD), it isn't bound by that HUD's height lock — a
// clean fixed height sized for ThreatSlots rows + a title is used, and the "Show threat / aggro list" toggle
// takes full effect live (SetVisible on the window, no reload caveat).
public sealed partial class Plugin
{
    private IWindowControl _threatWindow = null!;   // registered in the ctor; auto-shows on target when show_threat is on

    // Threat/Aggro row-size multiplier. Baked into the element sizes + the window's fixed W/H at build time, so a live
    // change re-registers the window (SetThreatScale → RebuildThreatWindow, debounced). Default 1.0 = unchanged.
    private float _threatScale        = 1f;
    private long  _threatScaleDirtyAtMs = -1;   // Environment.TickCount64 of the last slider change; -1 = idle

    // Config-backed: show the "Threat / Aggro" title row (default ON). Read live each frame by the title's
    // ConditionalElement, so the settings toggle hides/shows the header with no window rebuild (rows shift up).
    private bool  _showThreatTitle    = true;

    private void RegisterThreatWindow()
    {
        // Persisted toggle drives both the window's initial visibility and its ShouldRender gate.
        _showThreat = _cfg.Get<bool>("show_threat", true);
        // Persisted display mode: 0 = Top aggro (single top holder), 1 = Aggro List (default). Read by ThreatDisplay().
        _threatMode = _cfg.Get<int>("threat_mode", 1);
        // Row-size multiplier. Baked into the element sizes + the fixed window dims below, so a live change
        // re-registers the window (SetThreatScale → RebuildThreatWindow). Clamped to the slider band.
        _threatScale = System.Math.Clamp(_cfg.Get<float>("threat_scale", 1f), 1f, 2.5f);
        // Show/hide the title row (default ON). Live via the title's ConditionalElement (no rebuild).
        _showThreatTitle = _cfg.Get<bool>("show_threat_title", true);

        // Base (1×) dims scaled by _threatScale so the whole fixed-size window grows with the rows (this window is
        // not resizable, so there is no band — the DefaultRect W/H simply scale).
        float threatW = 260f * _threatScale;   // narrower than the Target HUD (name + a compact aggro bar + %)
        float threatH = 150f * _threatScale;   // Title + ThreatSlots rows + gaps + padding (fixed; auto-fits)

        // Default position tuned in-game (user's saved 2560x1440 layout: x=1716, y=4). Fixed 1440p-calibrated
        // pixel X (NOT ScreenWidth*frac): "reset all HUD" restores DefaultRect from a path where ScreenWidth is 0,
        // so 0*frac collapsed every window to x=0 — an absolute px restores correctly. y absolute. Own drag overrides.
        float x = 1716f;
        float y = 4f;

        _threatWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.threat",
                Title:       _loc.T("tl.window.threat"),
                DefaultRect: new WindowRect(x, y, threatW, threatH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false,
                // Auto-show: mirrors the Target HUD's gate (in-world, no menu/loading/blocking overlay, live target)
                // plus the show_threat toggle AND a non-empty hate list so it never shows an empty box. Layout-edit
                // mode overrides so the window can be positioned against its example rows without a live target.
                ShouldRender = () => _services.Windows.IsLayoutEditing
                                  || (_services.ClientState.Phase == GamePhase.World
                                      && (_services.ClientState.UiState & (GameUIState.Loading | GameUIState.Blocking | GameUIState.AnyMenu)) == 0
                                      && _showThreat
                                      && _targetInfo.Current.HasTarget
                                      && ThreatDisplay().Count > 0),
            },
            Root:    BuildThreatWindowRoot(),
            OnClose: () => { }));
        _windows.Add(_threatWindow);   // Dispose already loops _windows and Remove()s each

        // Initial visibility from the persisted toggle (the settings toggle flips it live via SetVisible).
        _threatWindow.SetVisible(_showThreat);
    }

    // Re-apply a new _threatScale by rebuilding the window Root at the new scale (element sizes + the fixed window
    // dims are baked at build time). Framework-sanctioned Remove()+Register() (mirrors RebuildBuffListWindow):
    // preserve ONLY the rect — NOT IsShown (at a rebuild moment with no live target it reads false and would strand
    // the window off); RegisterThreatWindow's SetVisible(_showThreat) + the ShouldRender gate handle visibility.
    private void RebuildThreatWindow()
    {
        var rect = _threatWindow.Rect;
        _windows.Remove(_threatWindow);
        _threatWindow.Remove();
        RegisterThreatWindow();
        if (rect.Width > 0f) _threatWindow.SetRect(rect);
    }

    // Threat row-size slider handler: update _threatScale live (knob + readout track the finger) and (re)arm the
    // debounce; the deferred TickThreatScaleRebuild does the single persist + rebuild once the drag settles.
    private void SetThreatScale(float v)
    {
        _threatScale = v;
        _threatScaleDirtyAtMs = System.Environment.TickCount64;
    }

    // Per-frame (OnTargetHudUpdate) settle check: once the drag has been quiet for ListScaleSettleMs, persist the
    // final value and rebuild the threat window ONCE at the new scale (mirrors TickListScaleRebuild).
    private void TickThreatScaleRebuild()
    {
        if (_threatScaleDirtyAtMs < 0) return;
        if (System.Environment.TickCount64 - _threatScaleDirtyAtMs < ListScaleSettleMs) return;
        _threatScaleDirtyAtMs = -1;
        _cfg.Set<float>("threat_scale", _threatScale);
        _cfg.Save();
        RebuildThreatWindow();
    }

    // Title + a fixed pool of ThreatSlots rows (name + compact aggro bar + %). Each row collapses to zero height
    // when it has no live entry (ConditionalElement on ThreatRowVisible), so short lists don't leave blank rows.
    // Row dimensions (name font, bar height + label font, bar cell width, gaps) scale together by _threatScale so
    // the list grows coherently; baked here at build time (SetThreatScale rebuilds the window to re-apply a change).
    // The TITLE is intentionally kept at a fixed size (excluded from scaling) so only the per-player rows grow.
    private HudElement BuildThreatWindowRoot()
    {
        float sc       = _threatScale;
        int   titlePx  = 14;   // fixed — title glyph does NOT scale with _threatScale (only the rows do)
        int   namePx   = (int)System.Math.Round(14 * sc);
        float barH     = 14f * sc;
        int   barFont  = (int)System.Math.Round(12 * sc);
        float barCellW = 84f * sc;
        float rowGap   = 6f * sc;
        float colGap   = 3f * sc;

        var rows = new HudElement[ThreatSlots + 1];
        // Wrapped so the "Show aggro title" toggle collapses the header live (rows shift up; window keeps its size).
        rows[0] = new ConditionalElement(() => _showThreatTitle,
            new TextElement(() => _loc.T("tl.window.threat"), Color: MutedColor, Emphasis: true, FontSize: titlePx));
        for (int s = 0; s < ThreatSlots; s++)
        {
            int idx = s;
            var row = new RowElement(new HudElement[]
            {
                new CellElement(
                    // Emphasis:true routes the name through the framework's STYLED text path (TryBuildEmphasisText →
                    // StyledSize), which honours TextElement.FontSize. The plain menu-surface text path
                    // (WindowBuilder.BuildText) hardcodes Scaled(14) and DROPS FontSize entirely, so without this the
                    // name never grew with _threatScale while the bar + % (bar LabelFontSize path) did. Bold also
                    // matches the title's styling. (The bar labels don't need it — LabelFontSize is honoured directly.)
                    new TextElement(() => ThreatName(idx), Color: () => ThreatRowColor(idx), FontSize: namePx, NoWrap: true, Emphasis: true),
                    Weight: 1f),
                new CellElement(
                    new BarElement(() => ThreatFraction(idx), ThreatFill, () => ThreatPct(idx))
                    { Style = BarStyle.Modern, Height = barH, FillWidth = true, LabelFontSize = barFont, LabelInside = true },
                    Width: barCellW),
            }, Gap: rowGap);
            rows[s + 1] = new ConditionalElement(() => ThreatRowVisible(idx), row);
        }
        return new ColumnElement(rows, Gap: colGap) { Padding = 8 };
    }
}
