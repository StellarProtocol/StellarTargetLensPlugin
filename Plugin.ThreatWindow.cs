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

    private void RegisterThreatWindow()
    {
        // Persisted toggle drives both the window's initial visibility and its ShouldRender gate.
        _showThreat = _cfg.Get<bool>("show_threat", true);
        // Persisted display mode: 0 = Top aggro (single top holder), 1 = Aggro List (default). Read by ThreatDisplay().
        _threatMode = _cfg.Get<int>("threat_mode", 1);

        const float ThreatW = 260f;   // narrower than the Target HUD (name + a compact aggro bar + %)
        // Title + ThreatSlots rows (~20px each) + gaps + padding. Fixed height (not resizable) so it auto-fits.
        const float ThreatH = 150f;

        // Default position tuned in-game (user's saved 2560x1440 layout: x=1716, y=4). Fixed 1440p-calibrated
        // pixel X (NOT ScreenWidth*frac): "reset all HUD" restores DefaultRect from a path where ScreenWidth is 0,
        // so 0*frac collapsed every window to x=0 — an absolute px restores correctly. y absolute. Own drag overrides.
        float x = 1716f;
        float y = 4f;

        _threatWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.threat",
                Title:       _loc.T("tl.window.threat"),
                DefaultRect: new WindowRect(x, y, ThreatW, ThreatH),
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

    // Title + a fixed pool of ThreatSlots rows (name + compact aggro bar + %). Each row collapses to zero height
    // when it has no live entry (ConditionalElement on ThreatRowVisible), so short lists don't leave blank rows.
    private HudElement BuildThreatWindowRoot()
    {
        var rows = new HudElement[ThreatSlots + 1];
        rows[0] = new TextElement(() => _loc.T("tl.window.threat"), Color: MutedColor, Emphasis: true, FontSize: 14);
        for (int s = 0; s < ThreatSlots; s++)
        {
            int idx = s;
            var row = new RowElement(new HudElement[]
            {
                new CellElement(
                    new TextElement(() => ThreatName(idx), Color: () => ThreatRowColor(idx), FontSize: 14, NoWrap: true),
                    Weight: 1f),
                new CellElement(
                    new BarElement(() => ThreatFraction(idx), ThreatFill, () => ThreatPct(idx))
                    { Style = BarStyle.Modern, Height = 14f, FillWidth = true, LabelFontSize = 12, LabelInside = true },
                    Width: 84f),
            }, Gap: 6f);
            rows[s + 1] = new ConditionalElement(() => ThreatRowVisible(idx), row);
        }
        return new ColumnElement(rows, Gap: 3f) { Padding = 8 };
    }
}
