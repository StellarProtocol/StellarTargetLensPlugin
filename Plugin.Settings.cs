using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Settings window + desktop-launcher tile for Target Lens (mirrors StellarPositionPlugin's launcher pattern).
// A GlassMenu Tools window opened from the launcher, holding the plugin's user-facing toggles: master HUD
// enable and the three per-source (mine / others / monster) caster filters. Registered from the ctor after the trackers +
// Target HUD exist (so _targetHudWindow / _targetBuff are live).
public sealed partial class Plugin
{
    private IWindowControl _settingsWindow = null!;   // registered in RegisterSettings(); drained via _windows
    private IDisposable    _launcherEntry  = null!;   // desktop launcher tile; disposed in Dispose()
    private bool           _showMine    = true;       // config-backed: include effects the local player applied
    private bool           _showOthers  = true;       // config-backed: include effects a third party applied
    private bool           _showMonster = true;       // config-backed: include the target's own + unknown-source effects
    private bool           _showHidden  = true;       // config-backed: list + show internal/no-icon buffs (default ON)
    private bool           _hidePermanent = true;     // config-backed: drop permanent / no-timer effects (default ON)

    private void RegisterSettings()
    {
        _settingsWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.settings",
                Title:       _loc.T("tl.settings.title"),
                DefaultRect: new WindowRect(_services.Framework.ScreenWidth - 460f, 20f, 440f, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              // Settings only make sense in-world, never over a loading screen.
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                   && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: new ColumnElement(new HudElement[]
            {
                // ── Target HUD ───────────────────────────────────────────────────
                new SeparatorElement(),
                new TextElement(() => _loc.T("tl.section.targetHud"), Emphasis: true),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _targetHudOn, Set: v =>
                    {
                        _targetHudOn = v;
                        _targetHudWindow.SetVisible(v);
                        _cfg.Set<bool>("target_hud_on", v);
                        _cfg.Save();
                    }),
                    new TextElement(() => _loc.T("tl.toggle.showTargetHud")),
                }, Gap: 6f),
                new RowElement(new HudElement[]
                {
                    new TextElement(() => _loc.T("tl.label.buffDisplay")),
                    new DropdownElement(
                        Selected: () => _buffStyle,
                        Options:  () => BuffStyleOptions(),
                        OnSelect: SetBuffStyle,
                        Width:    200f),
                }, Gap: 6f),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _hidePermanent, Set: v =>
                    {
                        _hidePermanent = v;
                        _targetBuff.HidePermanent = v;
                        _cfg.Set<bool>("hide_permanent", v);
                        _cfg.Save();
                    }),
                    new TextElement(() => _loc.T("tl.toggle.hidePermanent")),
                }, Gap: 6f),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _showHidden, Set: SetShowHidden),
                    new TextElement(() => _loc.T("tl.toggle.showHidden")),
                }, Gap: 6f),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _showMine, Set: v =>
                    {
                        _showMine = v;
                        _targetBuff.ShowMine = v;
                        _cfg.Set<bool>("show_mine", v);
                        _cfg.Save();
                    }),
                    new TextElement(() => _loc.T("tl.toggle.showMine")),
                }, Gap: 6f),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _showOthers, Set: v =>
                    {
                        _showOthers = v;
                        _targetBuff.ShowOthers = v;
                        _cfg.Set<bool>("show_others", v);
                        _cfg.Save();
                    }),
                    new TextElement(() => _loc.T("tl.toggle.showOthers")),
                }, Gap: 6f),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _showMonster, Set: v =>
                    {
                        _showMonster = v;
                        _targetBuff.ShowMonster = v;
                        _cfg.Set<bool>("show_monster", v);
                        _cfg.Save();
                    }),
                    new TextElement(() => _loc.T("tl.toggle.showMonster")),
                }, Gap: 6f),
                new RowElement(new HudElement[]
                {
                    new ButtonElement(() => _loc.T("tl.button.selectEffects"), OnClick: () => _selectWindow.SetVisible(true)),
                    new TextElement(() => _loc.T("tl.desc.selectEffects")),
                }, Gap: 6f),

                // ── Threat / Aggro ───────────────────────────────────────────────
                new SeparatorElement(),
                new TextElement(() => _loc.T("tl.section.threat"), Emphasis: true),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _showThreat, Set: v =>
                    {
                        // The threat/aggro list is its own window now, so the toggle takes full effect live: flip
                        // the window's visibility straight away (no reserved-height reload caveat).
                        _showThreat = v;
                        _threatWindow.SetVisible(v);
                        _cfg.Set<bool>("show_threat", v);
                        _cfg.Save();
                    }),
                    new TextElement(() => _loc.T("tl.toggle.showThreat")),
                }, Gap: 6f),

                // ── Cast Bar ─────────────────────────────────────────────────────
                new SeparatorElement(),
                new TextElement(() => _loc.T("tl.section.castBar"), Emphasis: true),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _castBarOn, Set: v =>
                    {
                        // The boss cast bar is its own window, so the toggle takes full effect live: flip the
                        // window's visibility straight away (no reserved-height reload caveat).
                        _castBarOn = v;
                        _castBarWindow.SetVisible(v);
                        _cfg.Set<bool>("cast_bar_on", v);
                        _cfg.Save();
                    }),
                    new TextElement(() => _loc.T("tl.toggle.showCastBar")),
                }, Gap: 6f),

                // ── Boss Skill Timers ────────────────────────────────────────────
                new SeparatorElement(),
                new TextElement(() => _loc.T("tl.section.bossTimers"), Emphasis: true),
                new RowElement(new HudElement[]
                {
                    new ToggleElement(Label: () => "", Get: () => _bossTimerOn, Set: v =>
                    {
                        // The boss skill-timer list is its own window, so the toggle takes full effect live: flip the
                        // window's visibility straight away (no reserved-height reload caveat).
                        _bossTimerOn = v;
                        _bossTimerWindow.SetVisible(v);
                        _cfg.Set<bool>("boss_timer_on", v);
                        _cfg.Save();
                    }),
                    new TextElement(() => _loc.T("tl.toggle.showBossTimers")),
                }, Gap: 6f),
            }, Gap: 8f),
            OnClose: () => _settingsWindow.SetVisible(false)));
        _windows.Add(_settingsWindow);   // Dispose already loops _windows and Remove()s each

        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title:   _loc.T("tl.settings.title"),
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen:  () => _settingsWindow.SetVisible(true))
        { Group = LauncherGroup.Plugin,
          // Gameplay tool: only surface its launcher tile while in-world.
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });
    }

    // Buff/debuff display style selector (0 = Classic tiles in the Target HUD, 1 = List window, 2 = Off = no display).
    // Persists and takes effect LIVE: flip the List window's visibility straight away; the Target HUD's classic tile
    // grid reacts on its own via a ConditionalElement on _buffStyle==0 (see Plugin.TargetHud.cs), so switching updates
    // both sides at once. Off (2) hides both — the tile grid (==0 gate) and the List window (SetVisible only on ==1).
    private void SetBuffStyle(int style)
    {
        _buffStyle = style;
        _cfg.Set<int>("buff_style", style);
        _cfg.Save();
        _buffListWindow.SetVisible(style == 1);   // List → show the window; Classic/Off → hide it
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
