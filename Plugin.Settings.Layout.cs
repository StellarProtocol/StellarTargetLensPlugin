using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Settings window body, house style — the approved prototype's "PROPOSED" block (targetlens-settings.html):
//   ToolHeader: [40 px accent tile, white crosshair] [bold "Target Lens" / muted "N of 6 overlays on"]
//   left column : TARGET HUD (header chip only) · BUFFS / DEBUFFS (Display chips, Show title, Size, SHOW filters, Select)
//   right column: THREAT / AGGRO · CAST BAR · BOSS SKILL TIMERS · NEARBY MONSTERS (each: header On/Off chip + rows)
// Every side effect is the one the old checkbox window had (same config keys, same SetVisible calls, same debounced
// scale setters) — only the controls changed. Built ONCE; every Func re-pulls live state on the capped refresh.
public sealed partial class Plugin
{
    // Window 700 wide (prototype ≈ 700): body 676 after the fixed 12 px window padding, two FIXED 333 px columns + a
    // 10 px gap. Fixed cells, not weighted ones: weighted cells only split equally when both children prefer the same
    // width (KB Stellar-Design-Language PillPair), and the two columns hold different content.
    private const float SettingsWindowWidth = 700f;
    private const float SettingsColumnW     = (SettingsWindowWidth - 24f - SettingsGap) / 2f;   // 333
    private const float SettingsGap         = 10f;   // between header / columns / panels (the prototype's 10)

    private HudElement BuildSettingsRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader("header-24.png", () => _loc.T("tl.settings.title"), SettingsStatus),   // Plugin.Settings.Rows.cs
        new RowElement(new HudElement[]
        {
            new CellElement(new ColumnElement(new HudElement[]
            {
                BuildTargetHudSection(),
                BuildBuffsSection(),
            }, Gap: SettingsGap), Width: SettingsColumnW),
            new CellElement(new ColumnElement(new HudElement[]
            {
                BuildThreatSection(),
                BuildCastBarSection(),
                BuildBossTimerSection(),
                BuildNearbySection(),
            }, Gap: SettingsGap), Width: SettingsColumnW),
        }, Gap: SettingsGap),
    }, Gap: SettingsGap);

    // ── left column ───────────────────────────────────────────────────────────────────────────────────────────────
    // TARGET HUD: just the caption + hint + the On/Off chip (the HUD itself has no options here).
    private HudElement BuildTargetHudSection() => SectionPanel(() => _loc.T("tl.section.targetHud"),
        () => _loc.T("tl.hint.targetHud"),
        OnOffChip(() => _targetHudOn, v =>
        {
            _targetHudOn = v;
            _targetHudWindow.SetVisible(v);
            _cfg.Set<bool>("target_hud_on", v);
            _cfg.Save();
        }),
        Array.Empty<HudElement>());

    // BUFFS / DEBUFFS. No header chip: the Display choice (Classic / List / Off) is the on/off. "Show title" and "Size"
    // only apply to the List window, so they are RECESSED (not hidden) unless Display = List. The SHOW filters and the
    // effect picker apply to both Classic and List and stay live.
    private HudElement BuildBuffsSection()
    {
        Func<bool> listMode = () => _buffStyle == 1;
        return SectionPanel(() => _loc.T("tl.section.buffs"), () => _loc.T("tl.hint.buffs"), null, new HudElement[]
        {
            ChoiceRow("tl.row.display", "tl.help.buffDisplay", "tl.section.buffs", new HudElement[]
            {
                ChoiceChip("tl.choice.classic", () => _buffStyle == 0, () => SetBuffStyle(0), BuffClassicChipW),
                ChoiceChip("tl.choice.list",    () => _buffStyle == 1, () => SetBuffStyle(1), ListChipW),
                ChoiceChip("tl.choice.off",     () => _buffStyle == 2, () => SetBuffStyle(2), OffChoiceChipW),
            }),
            // "Show title" — the List window's header row. Live via the title's ConditionalElement (no window rebuild).
            ChipRow("tl.row.showTitle", "tl.help.buffTitle", "tl.section.buffs", () => _showEffectTitle, v =>
            {
                _showEffectTitle = v;
                _cfg.Set<bool>("show_effect_title", v);
                _cfg.Save();
            }, listMode),
            // List-mode row size (1.0–2.5x): the drag only stashes the value + readout; the buff-list window is rebuilt
            // ONCE after the slider settles (SetListScale → TickListScaleRebuild), so a live drag never tears it down.
            SliderRow("tl.row.size", "tl.help.buffSize", "tl.section.buffs", () => _listScale, SetListScale, listMode),
            SubCaption("tl.sub.show"),
            ChipRow("tl.row.byMe", "tl.help.byMe", "tl.section.buffs", () => _showMine, v =>
            {
                _showMine = v;
                _targetBuff.ShowMine = v;
                _cfg.Set<bool>("show_mine", v);
                _cfg.Save();
            }),
            ChipRow("tl.row.byOthers", "tl.help.byOthers", "tl.section.buffs", () => _showOthers, v =>
            {
                _showOthers = v;
                _targetBuff.ShowOthers = v;
                _cfg.Set<bool>("show_others", v);
                _cfg.Save();
            }),
            ChipRow("tl.row.monster", "tl.help.monster", "tl.section.buffs", () => _showMonster, v =>
            {
                _showMonster = v;
                _targetBuff.ShowMonster = v;
                _cfg.Set<bool>("show_monster", v);
                _cfg.Save();
            }),
            ChipRow("tl.row.hidden", "tl.help.hidden", "tl.section.buffs", () => _showHidden, SetShowHidden),
            ChipRow("tl.row.hidePermanent", "tl.help.hidePermanent", "tl.section.buffs", () => _hidePermanent, v =>
            {
                _hidePermanent = v;
                _targetBuff.HidePermanent = v;
                _cfg.Set<bool>("hide_permanent", v);
                _cfg.Save();
            }),
            SelectEffectsRow(),
        });
    }

    // ── right column ──────────────────────────────────────────────────────────────────────────────────────────────
    // Each overlay is its own window, so every header chip takes full effect live: flip that window's visibility
    // straight away (no reserved-height reload caveat). While a section is off its rows are recessed (values kept).
    private HudElement BuildThreatSection()
    {
        Func<bool> on = () => _showThreat;
        return SectionPanel(() => _loc.T("tl.section.threat"), () => SectionOffHint(_showThreat),
            OnOffChip(on, v =>
            {
                _showThreat = v;
                _threatWindow.SetVisible(v);
                _cfg.Set<bool>("show_threat", v);
                _cfg.Save();
            }),
            new HudElement[]
            {
                // ThreatDisplay() reads _threatMode each frame → the rows switch live, no window-size change.
                ChoiceRow("tl.row.display", "tl.help.threatDisplay", "tl.section.threat", new HudElement[]
                {
                    ChoiceChip("tl.choice.topAggro", () => _threatMode == 0, () => SetThreatMode(0), TopAggroChipW, on),
                    ChoiceChip("tl.choice.list",     () => _threatMode == 1, () => SetThreatMode(1), ListChipW, on),
                }, on),
                // The threat window's header row. Live via the title's ConditionalElement (no rebuild).
                ChipRow("tl.row.showTitle", "tl.help.threatTitle", "tl.section.threat", () => _showThreatTitle, v =>
                {
                    _showThreatTitle = v;
                    _cfg.Set<bool>("show_threat_title", v);
                    _cfg.Save();
                }, on),
                // Rebuilt ONCE after the slider settles (SetThreatScale → TickThreatScaleRebuild).
                SliderRow("tl.row.size", "tl.help.threatSize", "tl.section.threat", () => _threatScale, SetThreatScale, on),
            });
    }

    private HudElement BuildCastBarSection()
    {
        Func<bool> on = () => _castBarOn;
        return SectionPanel(() => _loc.T("tl.section.castBar"), () => SectionOffHint(_castBarOn),
            OnOffChip(on, v =>
            {
                _castBarOn = v;
                _castBarWindow.SetVisible(v);
                _cfg.Set<bool>("cast_bar_on", v);
                _cfg.Save();
            }),
            new HudElement[]
            {
                // Rebuilt ONCE after the slider settles (SetCastScale → TickCastScaleRebuild).
                SliderRow("tl.row.size", "tl.help.castSize", "tl.section.castBar", () => _castScale, SetCastScale, on),
            });
    }

    private HudElement BuildBossTimerSection()
    {
        Func<bool> on = () => _bossTimerOn;
        return SectionPanel(() => _loc.T("tl.section.bossTimers"), () => SectionOffHint(_bossTimerOn),
            OnOffChip(on, v =>
            {
                _bossTimerOn = v;
                _bossTimerWindow.SetVisible(v);
                _cfg.Set<bool>("boss_timer_on", v);
                _cfg.Save();
            }),
            new HudElement[]
            {
                ChipRow("tl.row.showTitle", "tl.help.bossTitle", "tl.section.bossTimers", () => _showBossTimerTitle, v =>
                {
                    _showBossTimerTitle = v;
                    _cfg.Set<bool>("show_bosstimer_title", v);
                    _cfg.Save();
                }, on),
                // Rebuilt ONCE after the slider settles (SetBossTimerScale → TickBossTimerScaleRebuild).
                SliderRow("tl.row.size", "tl.help.bossSize", "tl.section.bossTimers", () => _bossTimerScale,
                    SetBossTimerScale, on),
            });
    }

    private HudElement BuildNearbySection()
    {
        Func<bool> on = () => _nearbyOn;
        return SectionPanel(() => _loc.T("tl.section.nearby"), () => SectionOffHint(_nearbyOn),
            OnOffChip(on, v =>
            {
                _nearbyOn = v;
                _nearbyWindow.SetVisible(v);
                _cfg.Set<bool>("nearby_on", v);
                _cfg.Save();
            }),
            new HudElement[]
            {
                ChipRow("tl.row.showTitle", "tl.help.nearbyTitle", "tl.section.nearby", () => _showNearbyTitle, v =>
                {
                    _showNearbyTitle = v;
                    _cfg.Set<bool>("show_nearby_title", v);
                    _cfg.Save();
                }, on),
                // Cap the list by the player's lock range (LockCancelRange) instead of the fixed ~60 m. Pushed into the
                // tracker, which reads LockableRangeOnly each poll → live, no rebuild. Config key keeps its old name.
                ChipRowLive(LockRangeLabel, "tl.row.lockRange", "tl.help.lockRange", "tl.section.nearby",
                    () => _nearbyLockableOnly, v =>
                    {
                        _nearbyLockableOnly = v;
                        _nearby.LockableRangeOnly = v;
                        _cfg.Set<bool>("nearby_targetable_only", v);
                        _cfg.Save();
                    }, on),
                // Rebuilt ONCE after the slider settles (SetNearbyScale → TickNearbyScaleRebuild).
                SliderRow("tl.row.size", "tl.help.nearbySize", "tl.section.nearby", () => _nearbyScale, SetNearbyScale, on),
            });
    }
}
