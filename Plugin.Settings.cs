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

    // Debounced List-scale rebuild. SliderElement exposes only a continuous Set (no release/commit callback), so a
    // live drag fires SetListScale many times/sec. Rebuilding the buff-list window (Remove()+Register()) on EVERY
    // tick tore it down continuously, so it appeared hidden for the whole drag. Instead each change stashes the new
    // value + a timestamp; the per-frame tick (OnTargetHudUpdate → TickListScaleRebuild) fires ONE rebuild once the
    // slider settles (~200ms after the last change ≈ mouse release). Mirrors the framework UI-scale slider, which
    // also defers its canvas repack to release (ThemesPanel.PollEditorUgui), and Maestro's seek-debounce idiom.
    private long           _listScaleDirtyAtMs = -1;  // Environment.TickCount64 of the last slider change; -1 = idle
    private const long     ListScaleSettleMs   = 200; // quiet time after the last change before the single rebuild

    private void RegisterSettings()
    {
        _settingsWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.settings",
                Title:       _loc.T("tl.settings.title"),
                // Fixed 1440p-calibrated pixel default (2560 − 460 = 2100). A literal, NOT ScreenWidth −
                // 460: the framework's "reset all HUD" path reads ScreenWidth as 0, which would push X to
                // −460 (off-screen left). A constant survives reset. Height 0f = auto-fit.
                DefaultRect: new WindowRect(2100f, 20f, 440f, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              // Settings only make sense in-world, never over a loading screen.
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                   && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: new ColumnElement(new HudElement[]
            {
                // ── Target HUD ───────────────────────────────────────────────────
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
                // List-mode row-size slider (1.0–2.5x). Affects the List style only, so the row is shown ONLY while
                // the display mode is List (_buffStyle == 1) — hidden for Classic (0) and Off (2); the predicate is
                // re-evaluated each frame so switching the dropdown shows/hides it live. The drag itself only stashes
                // the value + a readout; the buff-list window is rebuilt ONCE after the slider settles (SetListScale →
                // TickListScaleRebuild), so a live drag never tears the window down. Readout mirrors CooldownBar's slider.
                new ConditionalElement(() => _buffStyle == 1, new RowElement(new HudElement[]
                {
                    new TextElement(() => _loc.T("tl.label.effectSize")),
                    new SpacerElement(Width: 0f),
                    new TextElement(() => $"{_listScale:0.0}x"),
                    new SliderElement(() => _listScale, SetListScale, Min: 1.0f, Max: 2.5f) { Width = 120f },
                }, Gap: 6f)),
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
                new RowElement(new HudElement[]
                {
                    new TextElement(() => _loc.T("tl.label.threatMode")),
                    new DropdownElement(
                        Selected: () => _threatMode,
                        Options:  () => ThreatModeOptions(),
                        OnSelect: SetThreatMode,
                        Width:    200f),
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

    // Target Effects LIST row-size multiplier (List style only). The element sizes (icon, cell width, bar height,
    // font, row stride) are baked into the window Root at build time, so applying a new scale needs a window rebuild
    // (RebuildBuffListWindow) — but SliderElement has no release callback, so a drag calls this many times/sec.
    // Rebuilding per tick tore the window down for the whole drag. Instead: update _listScale live so the knob + "x"
    // readout track the finger, then (re)arm a debounce timestamp; the deferred TickListScaleRebuild does the single
    // persist + rebuild once the drag settles (≈ on release). Classic tiles are untouched.
    private void SetListScale(float v)
    {
        _listScale = v;                                // live value → the slider knob + readout track the drag
        _listScaleDirtyAtMs = Environment.TickCount64; // (re)arm; each change supersedes the prior pending rebuild
    }

    // Per-frame (OnTargetHudUpdate) settle check for the List-scale slider: once the drag has been quiet for
    // ListScaleSettleMs, persist the final value and rebuild the buff-list window ONCE at the new scale. Persisting
    // HERE (not on every drag frame) avoids a config write per tick and mirrors the framework's persist-on-release.
    private void TickListScaleRebuild()
    {
        if (_listScaleDirtyAtMs < 0) return;
        if (Environment.TickCount64 - _listScaleDirtyAtMs < ListScaleSettleMs) return;
        _listScaleDirtyAtMs = -1;
        _cfg.Set<float>("list_scale", _listScale);
        _cfg.Save();
        RebuildBuffListWindow();
    }

    // Threat display-mode selector (0 = Top aggro = only the single top holder, 1 = Aggro List = Top-N + appended local).
    // Persists and takes effect live — ThreatDisplay() reads _threatMode each frame, so the window's rows update on the
    // next frame with no reload; row count collapses via the per-row ConditionalElement, so no window-size change.
    private void SetThreatMode(int mode)
    {
        _threatMode = mode;
        _cfg.Set<int>("threat_mode", mode);
        _cfg.Save();
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
