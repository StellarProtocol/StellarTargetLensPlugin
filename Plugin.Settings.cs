using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Settings window + desktop-launcher tile for Target Lens (mirrors StellarPositionPlugin's launcher pattern).
// A GlassMenu Tools window opened from the launcher, holding the plugin's user-facing toggles: master HUD
// enable and the "only show effects I applied" caster filter. Registered from the ctor after the trackers +
// Target HUD exist (so _targetHudWindow / _targetBuff are live).
public sealed partial class Plugin
{
    private IWindowControl _settingsWindow = null!;   // registered in RegisterSettings(); drained via _windows
    private IDisposable    _launcherEntry  = null!;   // desktop launcher tile; disposed in Dispose()
    private bool           _targetHudOnlyMine;        // config-backed: filter effect tiles to caster == local player
    private bool           _breakDiag;                // config-backed: TEMPORARY break-gauge diagnostic logging
    private bool           _showHidden;               // config-backed: list + show internal/no-icon buffs (default OFF)

    private void RegisterSettings()
    {
        _settingsWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.settings",
                Title:       "Target Lens",
                DefaultRect: new WindowRect(_services.Framework.ScreenWidth - 460f, 20f, 440f, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              // Settings only make sense in-world, never over a loading screen.
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                   && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: new ColumnElement(new HudElement[]
            {
                new SeparatorElement(),
                new TextElement(() => "Target Lens", Emphasis: true),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _targetHudOn, Set: v =>
                    {
                        _targetHudOn = v;
                        _targetHudWindow.SetVisible(v);
                        _cfg.Set<bool>("target_hud_on", v);
                        _cfg.Save();
                    }),
                    new TextElement(() => "Show Target HUD (top-right overlay)"),
                }, Gap: 6f),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _targetHudOnlyMine, Set: v =>
                    {
                        _targetHudOnlyMine = v;
                        _targetBuff.OnlyMine = v;
                        _cfg.Set<bool>("target_hud_only_mine", v);
                        _cfg.Save();
                    }),
                    new TextElement(() => "Only show effects I applied"),
                }, Gap: 6f),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _breakDiag, Set: v =>
                    {
                        _breakDiag = v;
                        _targetInfo.BreakDiag = v;
                        _cfg.Set<bool>("break_diag", v);
                        _cfg.Save();
                    }),
                    new TextElement(() => "Break-gauge diagnostic (log to BepInEx)"),
                }, Gap: 6f),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _showHidden, Set: SetShowHidden),
                    new TextElement(() => "Show hidden effects"),
                }, Gap: 6f),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _showThreat, Set: v =>
                    {
                        // Persist + flip the HUD gate live. The block shows/hides immediately, but the window's
                        // reserved height only re-fits on the next load (height is locked at registration — see
                        // BuildTargetHudThreatBlock notes), so a mid-session enable may lack reserved space until reload.
                        _showThreat = v;
                        _cfg.Set<bool>("show_threat", v);
                        _cfg.Save();
                    }),
                    new TextElement(() => "Show threat / aggro list"),
                }, Gap: 6f),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _threatDiag, Set: v =>
                    {
                        _threatDiag = v;
                        _targetInfo.ThreatDiag = v;
                        _cfg.Set<bool>("threat_diag", v);
                        _cfg.Save();
                    }),
                    new TextElement(() => "Threat diagnostic (log to BepInEx)"),
                }, Gap: 6f),
                new SeparatorElement(),
                new RowElement(new HudElement[]
                {
                    new ButtonElement(() => "Select effects…", OnClick: () => _selectWindow.SetVisible(true)),
                    new TextElement(() => "Choose which buffs/debuffs appear on the HUD"),
                }, Gap: 6f),
            }, Gap: 8f),
            OnClose: () => _settingsWindow.SetVisible(false)));
        _windows.Add(_settingsWindow);   // Dispose already loops _windows and Remove()s each

        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title:   "Target Lens",
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen:  () => _settingsWindow.SetVisible(true))
        { Group = LauncherGroup.Plugin,
          // Gameplay tool: only surface its launcher tile while in-world.
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });
    }

    // Master "Show hidden effects" toggle. Persists, flips the tracker's list source (full vs display-filtered),
    // and invalidates the lazily-loaded picker tables so an already-open Select window repopulates live — the
    // Name/Icon filter in LoadSelectBuffTable depends on this flag.
    private void SetShowHidden(bool on)
    {
        _showHidden = on;
        _cfg.Set<bool>("show_hidden", on);
        _cfg.Save();
        _targetBuff.ShowHidden = on;
        _buffTabLoaded = false;
        EnsureBuffTabLoaded();
        ApplyDebuffTabFilter(_dtFilter);
        ApplyBuffTabFilter(_btFilter);
    }
}
