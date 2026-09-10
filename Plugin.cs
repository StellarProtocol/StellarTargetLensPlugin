using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Target Lens — a standalone, auto-showing combat-target HUD overlay extracted from the Experiment plugin.
// The top-right overlay (portrait / name / HP / break gauge / merged buff+debuff tiles) is built by the moved
// Plugin.TargetHud.cs / Plugin.TargetHudTooltip.cs partials and driven by the two trackers constructed here.
// There is no in-game menu or launcher tile: the HUD auto-shows whenever the game has a locked target, and the
// plugin is enabled/disabled from the desktop launcher.
public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "Target Lens";

    private readonly IPluginServices      _services;
    private readonly ILocalization        _loc;      // i18n: consumes IPluginServices.Localization (Lang/*.json catalog)
    private readonly IConfigSection       _cfg;
    private readonly List<IWindowControl> _windows = new();

    private TargetInfoTracker _targetInfo = null!;   // constructed in the ctor, before the HUD is registered
    private TargetBuffTracker _targetBuff = null!;   // constructed in the ctor, right after _targetInfo
    private BossDbmTracker    _bossDbm    = null!;   // reads the game's DBM boss-skill countdown list (target-independent)
    private IHotkeyAction     _lockAction = null!;   // rebindable hotkey: lock/unlock every overlay onto the current target

    // Theme muted-text colour helper (used by the moved Target HUD partials; copied from the old Plugin.FightRes.cs).
    private Func<ColorRgba?> MutedColor => () => (ColorRgba?)_services.Theme.Colors.TextMuted;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc = services.Localization;   // framework auto-loads the embedded Stellar.TargetLens.Lang.<locale>.json catalog
        _cfg = _services.Config.GetSection("settings");

        // Buff name/type/source-skill resolution used by the target buff tracker (own Harmony host, auto-unpatched).
        BuffTrackPatch.Install(_services.Harmony.Create("buff"), _services.Log.Info);
        // Boss DBM (deadly-skill) capture — postfix on DBMMgr.onDBMDatacChanged feeds BossDbmTracker (own host).
        DbmPatch.Install(_services.Harmony.Create("dbm"), _services.Log.Info);
        // Cast/channel capture — postfix on EntityExtensions.SetSingGuide (per-frame bar producer; beginSingGuide is
        // inlined) feeds the cast-bar read for ANY target (boss/elite/normal mob). Own Harmony host, auto-unpatched.
        CastPatch.Install(_services.Harmony.Create("cast"), _services.Log.Info);

        _targetInfo = new TargetInfoTracker(_services);
        _targetBuff = new TargetBuffTracker(_services, _targetInfo);
        _bossDbm    = new BossDbmTracker(_services);   // global DBM boss-skill list (not tied to the current target)

        // User effect-selection (which buffs/debuffs appear on the HUD), persisted in its own config section and
        // pushed into the tracker so RebuildLists honours it. Registered as a picker window below.
        _selCfg    = _services.Config.GetSection("select");
        _selection = TargetEffectSelection.Load(_selCfg);
        _targetBuff.Selection = _selection;

        RegisterTargetHud();          // top-right auto-showing combat-target overlay (reuses the two trackers above)
        RegisterTargetHudTooltip();   // click-to-info tooltip for the HUD's buff/debuff tiles
        RegisterThreatWindow();       // standalone auto-showing threat/aggro window (its own gated HUD overlay)
        RegisterBuffListWindow();     // standalone "List" buff/debuff style window (alternative to the classic tiles)
        RegisterBossTimerWindow();    // standalone boss skill-timer list (our replica of the game's native DBM list)
        RegisterCastBarWindow();      // standalone boss cast/chanting bar overlay (auto-shows only while a boss channels)
        _services.Framework.Update += OnTargetHudUpdate;

        // Load the three per-source caster filters and push them into the tracker, then register the settings window
        // + launcher tile (needs _targetBuff / _targetHudWindow, which the calls above have now built). Defaults are
        // all true → show everything, reproducing the prior default (old "only mine" filter shipped OFF).
        _showMine    = _cfg.Get<bool>("show_mine",    true);
        _showOthers  = _cfg.Get<bool>("show_others",  true);
        _showMonster = _cfg.Get<bool>("show_monster", true);
        _targetBuff.ShowMine    = _showMine;
        _targetBuff.ShowOthers  = _showOthers;
        _targetBuff.ShowMonster = _showMonster;
        _showHidden            = _cfg.Get<bool>("show_hidden", true);    // default ON: show hidden buffs out of the box
        _targetBuff.ShowHidden = _showHidden;
        _hidePermanent            = _cfg.Get<bool>("hide_permanent", true);   // default ON: permanent/no-timer effects hidden
        _targetBuff.HidePermanent = _hidePermanent;
        RegisterSettings();
        RegisterSelectWindow();   // effect picker opened from the settings window's "Select effects…" button

        // Rebindable hotkey to lock/unlock the HUD onto the current target (display-only — see Plugin.Lock.cs).
        _lockAction = _services.Hotkeys.DeclareAction(
            new HotkeyAction(
                Id:               "targetlens.locktarget",
                Description:      _loc.T("tl.hotkey.lockTarget"),
                SuggestedDefault: null),   // no default chord — user binds it themselves in the launcher
            callback: ToggleHudLock);

        _services.Log.Info("[TargetLens] constructed");
    }

    // Per-frame tick: re-assert the Target HUD tooltip's cursor rect after the destroy-on-hide remount, and drive the
    // four debounced size sliders (each fires ONE window rebuild after its drag settles — see TickListScaleRebuild).
    // One shared settle-tick path for every size slider rather than four framework subscriptions.
    private void OnTargetHudUpdate(float dt)
    {
        TickTargetTipPlace();
        TickListScaleRebuild();
        TickThreatScaleRebuild();
        TickCastScaleRebuild();
        TickBossTimerScaleRebuild();
    }

    public void Dispose()
    {
        try { _services.Framework.Update -= OnTargetHudUpdate; } catch { }
        BuffTrackPatch.Uninstall();
        CastPatch.Uninstall();
        try { _lockAction?.Dispose(); } catch { }
        _launcherEntry?.Dispose();
        _monIcon?.Dispose();
        foreach (var w in _windows) w.Remove();
    }

    // Icon manifest-resource handling (mirrors StellarPositionPlugin). Kept available for the desktop launcher's
    // plugin tile, which discovers the icon by its manifest-resource name.
    private static byte[]? LoadIconPng()
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.TargetLens.targetlens-icon.png");
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
