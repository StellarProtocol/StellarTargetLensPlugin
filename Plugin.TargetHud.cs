using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Top-right combat-target HUD overlay. Auto-shows in-world whenever the game has a locked target,
// mirroring the draggable "Target / Monster Info" menu window's data (portrait / name / HP / distance /
// effects). Reuses _targetInfo + _targetBuff — no new data layer. Registered from the ctor right after the
// trackers are built; toggled on/off from BuildTargetRoot().
//
// 2026-11-18 redesign (user-approved "Resonance Glass", _scratch/prototypes/targetlens-bossbar-APPROVED.md): a
// fixed-colour 3-slice frame art at 2× (150 px tall) with the portrait, rank badge, name, distance, HP bar and break
// gauge placed into the art's slots — layout in Plugin.TargetHud.Frame.cs. The target's effects are NOT drawn here:
// the standalone Target Effects window shows them (Plugin.BuffListWindow.cs; the in-HUD "Classic" tiles were removed
// 2026-11-20).
//
// v2.0.0 note: WindowSpec has NO HideUntilInWorld / AutoHideBehindGameMenus flags (they existed in v1.5.1).
// Both are omitted; the ShouldRender predicate below already gates to Phase==World and not-Loading.
public sealed partial class Plugin
{
    private IWindowControl _targetHudWindow = null!;   // registered in the ctor; auto-shows on target
    private bool           _targetHudOn;               // config-backed master enable (default on)

    private MonsterIconLoader _monIcon = null!;        // head-portrait loader (guarded; null icon on any failure)
    private UvRect            _portraitUv;             // atlas sub-rect for the current portrait

    // Local break-gauge fill tween — the raw attr snaps back to full after the recovery window; this reproduces
    // the game HUD's smooth 0→full refill over the monster's BreakingContinueTime.
    private readonly BreakGauge _breakGauge = new();

    // Manual per-effect overrides for buffs the game data resolves poorly (unnamed / no icon).
    // baseId → (display name for the tooltip, imagine skill id whose icon to draw). IconSkill 3948 =
    // "Arcane! Divine Reliance" imagine icon.
    private static readonly Dictionary<int, (string Name, int IconSkill)> EffectOverrides = new()
    {
        { 2110135, ("Rolora - Active Timer", 3948) },
        { 2110111, ("Rolora - Spell",        3948) },
    };

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    // Borderless chrome pads the content 12 px on every side (WindowBuilder.BuildBorderless) — the frame sits inside it.
    private const float HudPad = 12f;

    private void RegisterTargetHud()
    {
        _monIcon ??= new MonsterIconLoader(_services);   // ??= : a bar-size rebuild re-enters here

        // Boss bar size 0 Slim / 1 Medium (default) / 2 Large. Own key "boss_bar_size": the first build stored Slim as
        // "hud_bar_size" = 0 by default (never a deliberate pick), so that key only carries a Large choice (1 → 2) over;
        // everything else lands on the new default, Medium. Nothing is written until the user picks a size.
        int size = _cfg.Get<int>("boss_bar_size", -1);
        if (size < 0) size = _cfg.Get<int>("hud_bar_size", 0) == 1 ? 2 : 1;
        _hudSize = System.Math.Clamp(size, 0, 2);

        // MIN width = the art's two caps + a short middle (Slim 266 + 60, Medium 399 + 90, Large 532 + 120 — the badge
        // crosses the left seam and needs ≥ 40 / 60 / 80 px of middle) + padding. Defaults (frame width + 24 padding):
        // Medium 728 (the user-approved layout, 2026-11-19), Slim 485 / Large 971 (scaled by the frame height).
        float HudW = HudMinW();
        float HudDefaultW = Geo.DefaultW;
        // Resizable disables the content-auto-fit and fixes the height, so set an explicit height and lock it
        // below (MinHeight==MaxHeight) — width stays resizable, height cannot be dragged.
        //
        // The height is engine-locked for a Resizable window (MinHeight==MaxHeight can't change at runtime — see
        // WindowBuilder-Patterns.md), so it's chosen at registration from the bar size (a bar size change re-registers
        // the window: SetHudBarSize): 12 pad + frame (Slim 88 / Medium 132 / Large 176) + 12 pad = 112 / 156 / 200.
        float HudH = HudLockedH();
        // Default position = the user-approved layout (2026-11-19): horizontally CENTRED (WindowAnchor.Top, X offset 0 —
        // the framework centres the window's own width, so every bar size lands centred), top y = 15. A user's own
        // saved drag still overrides this.
        float x = 0f;
        float y = 15f;

        _targetHudWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.hud",
                Title:       _loc.T("tl.window.hud"),
                DefaultRect: new WindowRect(x, y, HudDefaultW, HudH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false,
                Anchor = WindowAnchor.Top,
                // Width-resizable; height LOCKED via MinHeight==MaxHeight so it can't be dragged.
                Resizable = true, MinWidth = HudW, MinHeight = HudH, MaxHeight = HudH,
                // Auto-show: only in-world, only when a target exists, and hidden while any menu/blocking
                // overlay or loading screen is up (replaces the v1.5.1 AutoHideBehindGameMenus flag). Layout-edit
                // mode is an override so the overlay (with example data) can be positioned without a live target.
                ShouldRender = () => _services.Windows.IsLayoutEditing
                                  || (_services.ClientState.Phase == GamePhase.World
                                      && (_services.ClientState.UiState & (GameUIState.Loading | GameUIState.Blocking | GameUIState.AnyMenu)) == 0
                                      && _targetInfo.Current.HasTarget),
            },
            Root:    BuildTargetHudRoot(),
            OnClose: () => { }));
        _windows.Add(_targetHudWindow);   // Dispose already loops _windows and Remove()s each

        // The toggle is the master enable — it controls whether the HUD may show at all.
        _targetHudOn = _cfg.Get<bool>("target_hud_on", true);
        _targetHudWindow.SetVisible(_targetHudOn);
    }

    private float HudMinW() => FrameCapsW + Geo.MidMin + 2f * HudPad;
    private float HudLockedH() => FrameH + 2f * HudPad;

    // Settings → Target HUD → Boss bar size (Slim / Medium / Large). Sizes, art, the locked height and the width floor are all
    // baked at build, so a change re-registers the window — the framework-sanctioned Remove()+Register() (mirrors
    // RebuildBossTimerWindow). Keeps the user's position and width (widened to the new size's floor if needed) and takes
    // the new size's locked height; visibility comes from RegisterTargetHud's SetVisible(_targetHudOn) + ShouldRender.
    private void SetHudBarSize(int size)
    {
        if (size == _hudSize) return;
        _cfg.Set<int>("boss_bar_size", size);
        _cfg.Save();
        var rect = _targetHudWindow.Rect;
        _windows.Remove(_targetHudWindow);
        _targetHudWindow.Remove();
        RegisterTargetHud();   // re-reads boss_bar_size → _hudSize
        if (rect.Width <= 0f) return;
        _targetHudWindow.SetRect(new WindowRect(rect.X, rect.Y, System.Math.Max(rect.Width, HudMinW()), HudLockedH()));
    }

    // The boss-bar frame alone (Plugin.TargetHud.Frame.cs); effects live in the Target Effects window.
    private HudElement BuildTargetHudRoot() => BuildTargetHudFrame();

    // ── Layout-edit example data ────────────────────────────────────────────────
    // While the layout editor (Shift+`) is active and no monster is locked, feed the getters representative
    // sample data so the overlay has visible content to size and position against. A real live target during
    // editing still wins — ShowExample is false whenever an actual target exists.
    private bool ShowExample => _services.Windows.IsLayoutEditing && !_targetInfo.Current.HasTarget;

    private static readonly TargetInfoTracker.Snapshot ExampleSnapshot = new(
        has: true, uuid: 0, name: "Example Boss", hp: 720000, maxHp: 1000000,
        stunned: 350, maxStunned: 500, level: 90, rank: "Boss", configId: 0,
        hasDistance: true, distance: 35f);

    // Three effects that exercise every icon path: a real buff-table icon, an override (Reliance ★), a letter fallback.
    private static readonly TargetBuffRow[] ExampleEffects =
    {
        new() { BaseId = 2110137, Name = "Void Corruption Power", Duration = 20000, SnapRemain = 14f, BuffType = 1, Layer = 3 },
        new() { BaseId = 2110135, Name = "Rolora - Active Timer", Duration = 20000, SnapRemain = 8f,  BuffType = 1 },
        new() { BaseId = 990001,  Name = "Sample Effect",         Duration = 30000, SnapRemain = 22f, BuffType = 0 },
    };

    private TargetInfoTracker.Snapshot Cur => ShowExample ? ExampleSnapshot : _targetInfo.Current;

    private IReadOnlyList<TargetBuffRow> CurEffects()
    {
        if (!ShowExample) { _targetBuff.Ensure(); return _targetBuff.All; }
        return ExampleEffectsNow();
    }

    // The sample rows, re-stamped so their captions read steady while editing. Also used by the Effects list, whose
    // example condition is wider than ShowExample (it shows them whenever its real list is empty — ListEffects).
    private static IReadOnlyList<TargetBuffRow> ExampleEffectsNow()
    {
        long now = System.Environment.TickCount64;
        foreach (var e in ExampleEffects) e.SnapTick = now;
        return ExampleEffects;
    }

    // ── Header / portrait / HP / distance ──────────────────────────────────────

    // Best-effort head portrait. Returns null → the generic silhouette shows in the disc instead (ShowPortraitPlaceholder).
    // The result is remembered so the placeholder layer's Visible check costs nothing (no second icon lookup); it reads
    // the previous apply's result, i.e. at most one ~10 Hz refresh behind — the portrait load is async anyway.
    private bool _hasPortrait;
    private object? GetMonsterPortrait()
    {
        var icon = _monIcon.GetIcon((int)Cur.ConfigId, out _portraitUv);
        _hasPortrait = icon != null;
        return icon;
    }

    // Silhouette only for a real target without a portrait; the layout-edit example boss keeps its empty disc.
    private bool ShowPortraitPlaceholder() => !_hasPortrait && !ShowExample;

    // Name only — the rank is shown on the badge. Level is intentionally omitted (AttrLevel never populates for monsters).
    private string TargetHudNameLine()
    {
        var s = Cur;
        if (!s.HasTarget) return _loc.T("tl.hud.unknown");
        return string.IsNullOrEmpty(s.Name) ? _loc.T("tl.hud.unknown") : s.Name;
    }

    private bool TargetHudHasRank()
    {
        var s = Cur;
        return s.HasTarget && !string.IsNullOrEmpty(s.Rank) && s.Rank != "Normal";
    }

    private bool TargetHudIsBoss()  { var s = Cur; return s.HasTarget && s.Rank == "Boss"; }
    private bool TargetHudIsElite() { var s = Cur; return s.HasTarget && s.Rank == "Elite"; }

    // Rank is game data ("Boss"/"Elite"/"Normal") kept as-is for the logic compares above; only the DISPLAYED
    // badge text is localized. Any other rank value falls back to the uppercased raw string (cached — no per-poll alloc).
    private string? _rankRaw, _rankUpper;
    private string TargetHudRankText()
    {
        if (!TargetHudHasRank()) return "";
        var rank = Cur.Rank;
        if (rank == "Boss")  return _loc.T("tl.rank.boss");
        if (rank == "Elite") return _loc.T("tl.rank.elite");
        if (rank != _rankRaw) { _rankRaw = rank; _rankUpper = rank.ToUpperInvariant(); }
        return _rankUpper!;
    }

    private float TargetHudHpFraction()
    {
        var s = Cur;
        return s.MaxHp > 0 ? Clamp01((float)s.Hp / s.MaxHp) : 0f;
    }

    // ── Break / stagger gauge (MaxStunned > 0 is the presence signal) ──
    // Any rank, as the old HUD did (in-game 2026-11-18: the training dummy has one). No gauge → the row stays empty.
    private bool TargetHudHasBreak()
    {
        var s = Cur;
        return s.HasTarget && s.MaxStunned > 0;
    }

    // Per-frame driver for the local break tween. Safe to call repeatedly — BreakGauge frame-gates internally.
    private void UpdateBreakGaugeState()
    {
        var s = Cur;
        float dur = _targetInfo.TryGetBreakingContinueTime(s.ConfigId, out var sec) ? sec : 8f; // fallback 8s on table miss
        _breakGauge.Update(s.Uuid, s.Stunned, s.MaxStunned, dur, System.Environment.TickCount64);
    }

    private float TargetHudBreakFraction()
    {
        UpdateBreakGaugeState();
        return _breakGauge.Fill;
    }

    // True during the local recovery window (gauge broken → refilling); drives which gauge layer renders.
    private bool TargetHudBroken()
    {
        UpdateBreakGaugeState();
        return _breakGauge.Broken;
    }
}
