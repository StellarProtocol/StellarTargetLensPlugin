using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Settings window + desktop-launcher tile for Target Lens, house style (Stellar Design Language; user-approved
/// prototype <c>_scratch\prototypes\targetlens-settings.html</c>, 2026-11-12). A ≈700 px GlassMenu window:
/// ToolHeader (accent crosshair tile + "N of 6 overlays on") → two balanced columns of section panels (left: TARGET
/// HUD, BUFFS / DEBUFFS; right: THREAT / AGGRO, CAST BAR, BOSS SKILL TIMERS, NEARBY MONSTERS). Every setting row ends in
/// a round "?" (help popover). Nothing is ever hidden: a row that doesn't apply right now (its section is off, or a
/// List-only buff option in Classic/Off) is RECESSED (rule 22) and keeps its value, so the window height never changes.
/// Layout: Plugin.Settings.Layout.cs; row builders + header status: Plugin.Settings.Rows.cs; help popover:
/// Plugin.Settings.Help.cs; open + centring (this window and the effect picker): Plugin.Settings.Open.cs; colours /
/// components: Plugin.Style*.cs. The setters below (and the per-window scale setters) are unchanged by the redesign.
/// </summary>
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
        // The help popover first: the settings rows' "?" fills read _helpWindow.IsShown.
        _helpWindow = RegisterHelpWindow();   // Plugin.Settings.Help.cs
        _windows.Add(_helpWindow);

        _settingsWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.settings",
                Title:       _loc.T("tl.settings.title"),
                // Fixed 1440p-calibrated pixel default. A literal, NOT derived from ScreenWidth: the framework's "reset
                // all HUD" path reads ScreenWidth as 0, which would push X off-screen. A constant survives reset (and
                // every open re-centres it anyway, Plugin.Settings.Open.cs). Height 0f = auto-fit: no row ever hides,
                // so the fitted height is the same in every state.
                DefaultRect: new WindowRect(930f, 200f, SettingsWindowWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            {
                TitleProvider = () => _loc.T("tl.settings.title"),   // live-localized; Title stays the fallback
                Draggable = true, Closable = true, StartVisible = false,
                // Settings only make sense in-world, never over a loading screen.
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
                // Framework pre-build (fw 2.30.0): ~20 rows of chips / sliders / "?"s are built at the title screen and
                // filled off-screen once in the world, so the first open is a re-show instead of a build hitch. The
                // value lambdas only read plugin fields (+ _loc), safe whenever the world is up. Content never changes
                // shape, so no PrebuildSignature (= filled once per world entry).
                Prebuild = true,
            },
            Root: BuildSettingsRoot(),   // Plugin.Settings.Layout.cs
            OnClose: CloseSettings));    // Plugin.Settings.Open.cs
        _windows.Add(_settingsWindow);   // Dispose already loops _windows and Remove()s each

        // Title stays the fixed literal "Target Lens" — the stable pin-identity key (ILauncher.cs:49-50) —
        // so a pinned tile survives a language change; TitleProvider carries the live-localized display.
        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title:   "Target Lens",
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen:  OpenSettings)
        { Group = LauncherGroup.Plugin,
          // Re-localize the tile DISPLAY on a language change; Title above never changes (pin identity).
          TitleProvider = () => _loc.T("tl.settings.title"),
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
