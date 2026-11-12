using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Effect-picker rows, house style. ONE scroll-windowed list serves both tabs: every slot reads the ACTIVE tab's filtered
/// arrays at <see cref="_selOffset"/>+slot. A row is a filled 26 px tile in a 28 px slot (<see cref="ListSlot"/>):
///   [16 px tick box] [game icon 20] [amber ★ for recommended groups] [name …] [muted ×N | "hidden" | green flash word]
/// The whole row is the click target (toggles every member id of the name group). Ticked = accent wash + accent box with
/// "✓"; in "All except ticked" a ticked row is one the HUD HIDES: red wash, red box with "✕", muted struck-through name
/// and "hidden" on the right. Hover follows the rounded tile (Bubble fill around a Bare selectable).
/// Per-refresh cost: each Func is O(1) — the group's ticked / recommended state is cached per slot and recomputed only
/// when the slot shows another group or <see cref="_selVersion"/> moved; the ×N label is a per-slot FmtCache.
/// </summary>
public sealed partial class Plugin
{
    private const float SelCheckBoxSize = 16f;
    private const float SelIconSize     = 20f;
    // Right column: the bold flash words (fil "Naidagdag" ≈ 66, th "นำออกแล้ว" ≈ 63) and "hidden" (id "tersembunyi"
    // ≈ 78 plain) fit, so the name column keeps one width on every row (house rule 10).
    private const float SelValueColW    = 80f;

    // ── active-tab views (no allocation: they return the existing arrays) ─────────────────────────────────────────
    private int      SelFiltCount   => _activeTab == 0 ? _dtFiltCount   : _btFiltCount;
    private int[]    SelFiltIds     => _activeTab == 0 ? _dtFiltIds     : _btFiltIds;
    private string[] SelFiltNames   => _activeTab == 0 ? _dtFiltNames   : _btFiltNames;
    private int[][]  SelFiltMembers => _activeTab == 0 ? _dtFiltMembers : _btFiltMembers;
    private string   SelFilter      => _activeTab == 0 ? _dtFilter      : _btFilter;
    private bool     SelExcludeMode => ActiveTabMode() == TrackMode.ExcludeBelow;

    // Bound once (a method-group conversion allocates a delegate each time it is written).
    private Func<int, bool>? _isDebuffTrackedFn, _isBuffTrackedFn;
    private Func<int, bool> SelIsTracked => _activeTab == 0
        ? _isDebuffTrackedFn ??= _selection.IsDebuffTracked
        : _isBuffTrackedFn ??= _selection.IsBuffTracked;

    // ── ticked GROUP counts per tab (header status, tab chips, footer, confirm) ──────────────────────────────────
    // A group counts once it is fully ticked (the row's tick box). Recounted only when _selVersion moved (a click, a
    // load, a re-filter) — a walk over the groups, never per refresh. Before the tables load, the raw id counts stand in.
    private int _tickCountVer = -1, _dtTicked, _btTicked;

    private void EnsureTickCounts()
    {
        if (_tickCountVer == _selVersion) return;
        _tickCountVer = _selVersion;
        if (!_buffTabLoaded) { _dtTicked = _selection.DebuffCount; _btTicked = _selection.BuffCount; return; }
        _dtTicked = CountTicked(_dtMembers, _dtCount, _isDebuffTrackedFn ??= _selection.IsDebuffTracked);
        _btTicked = CountTicked(_btMembers, _btCount, _isBuffTrackedFn ??= _selection.IsBuffTracked);
    }

    private static int CountTicked(int[][] members, int count, Func<int, bool> isTracked)
    {
        int n = 0;
        for (int i = 0; i < count; i++) if (GroupAllTracked(members[i], isTracked)) n++;
        return n;
    }

    private int SelActiveTicked { get { EnsureTickCounts(); return _activeTab == 0 ? _dtTicked : _btTicked; } }

    // ── per-slot cache ────────────────────────────────────────────────────────────────────────────────────────────
    // Keyed by the group's member array (a group's identity: the filter re-uses the same arrays) + _selVersion.
    private readonly int[]?[] _slotMembers = new int[]?[SettingsPoolSize];
    private readonly int[]    _slotVer     = new int[SettingsPoolSize];
    private readonly bool[]   _slotTicked  = new bool[SettingsPoolSize];
    private readonly bool[]   _slotRec     = new bool[SettingsPoolSize];
    private readonly FmtCache[] _slotVarFmt = NewFmtCaches(SettingsPoolSize);

    private static FmtCache[] NewFmtCaches(int n)
    {
        var a = new FmtCache[n];
        for (int i = 0; i < n; i++) a[i] = new FmtCache();
        return a;
    }

    private int[]? SlotMembers(int slot)
    {
        int i = _selOffset + slot;
        return i >= 0 && i < SelFiltCount ? SelFiltMembers[i] : null;
    }

    private void BindSlot(int slot)
    {
        var m = SlotMembers(slot);
        if (ReferenceEquals(m, _slotMembers[slot]) && _slotVer[slot] == _selVersion) return;
        _slotMembers[slot] = m;
        _slotVer[slot] = _selVersion;
        _slotTicked[slot] = m != null && GroupAllTracked(m, SelIsTracked);
        _slotRec[slot]    = m != null && IsRecommendedGroup(m);
    }

    private bool SlotTicked(int slot)      { BindSlot(slot); return _slotTicked[slot]; }
    private bool SlotRecommended(int slot) { BindSlot(slot); return _slotRec[slot]; }
    private bool SlotHidden(int slot)      => SelExcludeMode && SlotTicked(slot);   // the HUD hides this group

    // ── the tile ──────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildEffectTile(int slot) => new BubbleElement(new SelectableElement(
        new RowElement(new HudElement[]
        {
            new SpacerElement(2f, ListTileHeight),   // height strut + (with the 6 px gap) the 8 px left inset
            EffectTickBox(slot),
            // Fixed cell + centring Row: a bare picture in a Cell is stretched sideways (the icon-squish gotcha).
            new CellElement(new RowElement(new HudElement[]
                {
                    new GameTextureElement(() => SlotIcon(slot), (int)SelIconSize, (int)SelIconSize, () => _selUv[slot]),
                }, Gap: 0.01f, Justify: RowJustify.Center), Width: SelIconSize),
            // Curated "recommended" groups: an amber ★ (plain text — a bold ★ is untrusted). Not localized: a marker.
            new ConditionalElement(() => SlotRecommended(slot),
                new CellElement(new TextElement(() => "★", Color: () => CautionColor(), NoWrap: true), Width: 12f))
                { Prebuild = true },
            // Name: normal, or (a row the HUD hides) muted + struck through — two complete texts, one fixed slot.
            new CellElement(new ConditionalElement(() => SlotHidden(slot),
                new TextElement(() => SlotName(slot), Color: () => MutedText(), NoWrap: true)
                    { Ellipsize = true, Strikethrough = true },
                new TextElement(() => SlotName(slot), Color: () => TextColor(), NoWrap: true) { Ellipsize = true })
                { Prebuild = true }, Weight: 1f),
            // Right column: muted ×N / "hidden", or for ~1 s after a click on this group the bold green "Added" /
            // "Removed" (bold can't toggle live → two texts). Same fixed cell, so the swap never moves the name.
            new CellElement(new ConditionalElement(() => SlotFlashing(slot),
                new TextElement(SelFlashLabel, Color: () => SelFlashTextColor(), Align: TextAlign.Right, NoWrap: true,
                    FontSize: 13) { Bold = true },
                new TextElement(() => SlotRightLabel(slot), Color: () => MutedText(), Align: TextAlign.Right, NoWrap: true))
                { Prebuild = true }, Width: SelValueColW),
            new SpacerElement(2f, 1f),               // right inset
        }, Gap: 6f),
        OnClick: () => ToggleSlot(slot))
        { Bare = true, OnHover = over => SetSelSlotHover(slot, over) })
    {
        FillColorFn = () => Fade(EffectTileFill(slot)),
        CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
    };

    // Tile → accent wash (ticked) / red wash (ticked = hidden, "All except ticked") → green while its click flash runs;
    // hover lightens whichever.
    private ColorRgba EffectTileFill(int slot)
    {
        var fill = SlotFlashing(slot)  ? Over(SuccessColor() with { A = FlashWashAlpha }, TileColor())
                 : !SlotTicked(slot)   ? TileColor()
                 : SelExcludeMode      ? Over(DangerColor() with { A = DangerWashAlpha }, TileColor())
                 :                       Over(AccentColor() with { A = AccentWashAlpha }, TileColor());
        return SelSlotHovered(slot) ? HoverFill(fill) : fill;
    }

    // 16 px rounded tick box: ticked = solid accent + white "✓" (or solid red + "✕" when ticked means hidden), unticked
    // = an empty Neutral box. One element (live glyph + live fill), so ticking never rebuilds anything. Text.Height pins
    // the plain 14 px glyph's line box to 16, so the box stays exactly 16 × 16.
    private HudElement EffectTickBox(int slot) => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, SelCheckBoxSize),
            new TextElement(() => !SlotTicked(slot) ? string.Empty : SelExcludeMode ? "✕" : "✓",
                () => EnsureContrast(White, SelExcludeMode ? DangerColor() : AccentColor(), SolidMinContrast),
                Width: SelCheckBoxSize, Align: TextAlign.Center, NoWrap: true) { Height = SelCheckBoxSize },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Fade(!SlotTicked(slot) ? NeutralFill() : SelExcludeMode ? DangerColor() : AccentColor()),
        CornerRadius = 4f, PaddingX = 0f, PaddingY = 0f,
    }, Width: SelCheckBoxSize);

    private object? SlotIcon(int slot)
    {
        int i = _selOffset + slot;
        if (i >= SelFiltCount) { _selUv[slot] = default; return null; }
        return _services.GameAssets.LoadBuffIcon(SelFiltIds[i], out _selUv[slot]);
    }

    private string SlotName(int slot)
    {
        int i = _selOffset + slot;
        return i < SelFiltCount ? SelFiltNames[i] : string.Empty;
    }

    // "hidden" for a group the HUD hides; otherwise "×N" when same-name variants collapsed into this row (N > 1).
    private string SlotRightLabel(int slot)
    {
        if (SlotHidden(slot)) return _loc.T("tl.sel.hidden");
        var m = SlotMembers(slot);
        return m != null && m.Length > 1 ? _slotVarFmt[slot].Get(_loc.T("tl.group.variants"), m.Length) : string.Empty;
    }

    // ── hover (per pool slot) ─────────────────────────────────────────────────────────────────────────────────────
    // Keyed by the slot's LOGICAL row: when the slot is re-bound to another row (scroll, filter, tab), the flag resets
    // until the pointer re-enters, so a recycled slot never inherits the previous row's hover.
    private readonly bool[] _selSlotHover    = new bool[SettingsPoolSize];
    private readonly int[]  _selSlotHoverRow = new int[SettingsPoolSize];

    private void SetSelSlotHover(int slot, bool over)
    {
        _selSlotHover[slot] = over;
        _selSlotHoverRow[slot] = _selOffset + slot;
    }

    private bool SelSlotHovered(int slot)
    {
        if (_selSlotHoverRow[slot] != _selOffset + slot) { _selSlotHoverRow[slot] = _selOffset + slot; _selSlotHover[slot] = false; }
        return _selSlotHover[slot];
    }

    // ── click ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Toggle the whole name group. ON only when every member id is tracked (a partial group reads OFF, so one tap
    // selects all of it). Selection stays per-id: write every member id, so the HUD's per-id display path is unchanged.
    // No re-sort on click (rows never jump under the cursor); the next tab switch / search re-sorts ticked to the top.
    private void ToggleSlot(int slot)
    {
        int i = _selOffset + slot;
        if (i < 0 || i >= SelFiltCount) return;
        var members = SelFiltMembers[i];
        bool on = !GroupAllTracked(members, SelIsTracked);
        foreach (int id in members)
        {
            if (_activeTab == 0) _selection.SetDebuff(id, on);
            else                 _selection.SetBuff(id, on);
        }
        _selection.Save(_selCfg);
        _selVersion++;
        OnEffectToggled(members, SelFiltNames[i], on);   // Plugin.SelectWindow.Feedback.cs — row flash + footer line
    }
}
