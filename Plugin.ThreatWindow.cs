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

        // Default position = the user-approved layout (2560x1440, 2026-11-19: x=1653, y=13, w=260), Top-anchored: X is
        // the offset from the horizontally centred spot (1653 − (2560 − 260)/2 = 503), so it keeps its place beside the
        // centred boss bar at any resolution. A saved (user-moved) rect always wins over this.
        float x = 503f;
        float y = 13f;

        _threatWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.threat",
                Title:       _loc.T("tl.window.threat"),
                DefaultRect: new WindowRect(x, y, threatW, threatH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false,
                Anchor = WindowAnchor.Top,
                BackgroundColor = () => CrestPlateFill(OvThreat), BackgroundCornerRadius = CrestPlateRadius,   // Crest plate (Plugin.Crest.cs)
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

    // Crest look (Plugin.Crest.cs): caption "THREAT" + "N players" + hairline, then up to ThreatSlots rows of
    // [rank medallion] [full-width amber bar: name left, share % right]; your row carries a 1.5 px pale-gold ring.
    // Rows collapse when empty (ConditionalElement on ThreatRowVisible). Row sizes scale with _threatScale (baked at
    // build; SetThreatScale rebuilds); the caption keeps its fixed size.
    private HudElement BuildThreatWindowRoot()
    {
        float sc    = _threatScale;
        float barH  = 20f * sc;
        int   font  = (int)System.Math.Round(12 * sc);
        int   med   = MedalPx(sc);
        // Medallion art for this size, resolved once (the Live image compares by reference).
        var gold = CachedPng($"crest-med-gold-{med}.png");   var silver = CachedPng($"crest-med-silver-{med}.png");
        var bronze = CachedPng($"crest-med-bronze-{med}.png"); var plain = CachedPng($"crest-med-plain-{med}.png");
        int numPx = (int)System.Math.Round(11 * sc);

        var rows = new HudElement[ThreatSlots + 1];
        rows[0] = CrestCaption("tl.cap.threat", () => _threatHint.Get(_loc.T("tl.hint.players"), ThreatTotal()), () => _showThreatTitle);
        for (int s = 0; s < ThreatSlots; s++)
        {
            int idx = s;
            var medal = new LayerStackElement(
                new ImageElement(() => ThreatRank(idx) switch { 1 => gold, 2 => silver, 3 => bronze, _ => plain }, med, med) { Live = true, Crisp = true },
                new StackLayer(new TextElement(() => RankText(ThreatRank(idx)), Color: () => ThreatRank(idx) <= 3 ? MedalInk : CrestSoft,
                    Align: TextAlign.Center, FontSize: numPx, NoWrap: true) { Bold = true, CenterCaps = true }));
            var bar = CrestBarRings(
                CrestBar(() => ThreatFraction(idx), GradAmber, () => ThreatName(idx), () => ThreatPct(idx), barH, font, sc),
                sc, () => ThreatIsLocal(idx), CrestGoldHi);
            var row = new RowElement(new HudElement[] { new CellElement(medal, Width: med), new CellElement(bar, Weight: 1f) }, Gap: 6f * sc);
            rows[s + 1] = new ConditionalElement(() => ThreatRowVisible(idx), row);
        }
        return CrestFrame(new ColumnElement(rows, Gap: 3f * sc), OvThreat);
    }

    private static readonly ColorRgba MedalInk = CrestHex(0x1a1408);
    private readonly CountHint _threatHint = new();
}
