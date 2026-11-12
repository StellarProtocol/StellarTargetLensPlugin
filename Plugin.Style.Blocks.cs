using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// More house-kit building blocks (Stellar Design Language §4; reference StatInspector / Maestro Plugin.Style.Blocks.cs):
// embedded art, the accent icon tile, ToolHeader, the clickable 24 px chips (On/Off + choice), the round-knob slider, and
// a cached string formatter so the per-refresh text Funcs never build a string unless their inputs changed
// (Plugin-Performance-Rules §2).
public sealed partial class Plugin
{
    // ── embedded PNGs ─────────────────────────────────────────────────────────────────────────────────────────────
    // Read once per resource (csproj EmbeddedResource LogicalName "Stellar.TargetLens.<file>") and kept: ImageElement
    // Funcs are polled. A missing resource caches null (the element then draws nothing) instead of re-reading every poll.
    private static readonly Dictionary<string, byte[]?> _pngCache = new();

    private static byte[]? CachedPng(string file)
    {
        if (_pngCache.TryGetValue(file, out var png)) return png;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.TargetLens." + file);
            if (s != null) { using var ms = new System.IO.MemoryStream(); s.CopyTo(ms); png = ms.ToArray(); }
        }
        catch { png = null; }
        _pngCache[file] = png;
        return png;
    }

    // ── icon tile + tool header ───────────────────────────────────────────────────────────────────────────────────
    // A rounded tile in the LIVE theme accent with a flat white glyph centred on it (art can't follow the theme, so the
    // colour lives in the tile). The glyph is PRE-DRAWN at the exact size it is shown (Resources\header-<px>.png, each
    // hinted to whole pixels by tools\gen_header_icon.py) — never one master scaled down, which turns thin strokes soft
    // grey. 40 px tile → the 24 px glyph with 8 px all round. Radius ≈ 22 % of the side (9 at 40).
    private const float ToolIconSize  = 40f;
    private const int   ToolGlyphSize = 24;

    private HudElement IconBox(string glyphPng, float size, int glyph)
    {
        float pad = (size - glyph) / 2f;
        return new CellElement(new BubbleElement(
            new RowElement(new HudElement[] { new ImageElement(() => CachedPng(glyphPng), glyph, glyph) },
                Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(size * 0.22f), PaddingX = pad, PaddingY = pad,
        }, Width: size);
    }

    // Window title block: 40 px icon + bold title over a live status line (one line each, ellipsized, so the header
    // height never changes with the state or the locale).
    private HudElement ToolHeader(string glyphPng, Func<string> title, Func<string> status) => new RowElement(
        new HudElement[]
        {
            IconBox(glyphPng, ToolIconSize, ToolGlyphSize),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(title, Color: () => TextColor(), Emphasis: true, NoWrap: true) { Ellipsize = true },
                new TextElement(status, Color: () => MutedText(), NoWrap: true) { Ellipsize = true },
            }, Gap: 2f), Weight: 1f),
        }, Gap: 10f);

    // ── chips ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // A clickable 24 px chip in a fixed-width cell: Cell → round Bubble (fill + hover) → Bare Selectable → Row[24 px
    // strut, label]. Two COMPLETE chips (on = bold accent on a 22 % accent wash, off = muted on the Tile) are swapped by a
    // ConditionalElement wrapping the whole chip (bold can't toggle live; never a Conditional inside a bubble's measured
    // child), both the SAME width and sharing one HoverFlag, so a click never moves the row. Bubble.MaxWidth pins the
    // width: a MaxWidth-0 bubble under a Conditional caps itself at the 100 px default (MinimalNameplate, measured 64×24
    // in both states in-game 2026-11-07).
    // `enabled` (optional, polled): false = rule 22 RECESSED (sunken fill + faint label, same size), click swallowed, no
    // hover. The chip keeps showing its value — a recessed row still tells you what's saved.
    private const float ChipHeight = 24f;

    private HudElement ChipShell(HudElement label, Func<ColorRgba> fill, HoverFlag hover, Action onClick, float width,
        Func<bool> en)
        => new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipHeight),   // height strut = the side (house rule 4: no stacked spacers)
                label,
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (en()) onClick(); }) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(!en() ? DisabledFill(PanelColor()) : hover.On ? HoverFill(fill()) : fill()),
            CornerRadius = RoundRadius(ChipHeight), PaddingX = 0f, PaddingY = 0f, MaxWidth = width,
        };

    private ColorRgba ChipOnFill() => Over(AccentColor() with { A = AccentWashAlpha }, PanelColor());
    private ColorRgba ChipOnText() => EnsureContrast(AccentColor(), ChipOnFill(), SolidMinContrast);

    // One chip, two complete states. `onLabel` / `offLabel` are the label in each state (On / Off for a toggle, the
    // same text for a choice chip).
    private HudElement StateChip(Func<string> onLabel, Func<string> offLabel, Func<bool> on, Action click, float width,
        Func<bool>? enabled)
    {
        var hover = new HoverFlag();
        bool En() => enabled == null || enabled();
        return new CellElement(new ConditionalElement(on,
            ChipShell(DropBold(new TextElement(onLabel, Color: () => En() ? ChipOnText() : DisabledLabel(PanelColor()),
                    Width: width, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true }),
                ChipOnFill, hover, click, width, En),
            ChipShell(new TextElement(offLabel, Color: () => En() ? MutedText() : DisabledLabel(PanelColor()),
                    Width: width, Align: TextAlign.Center, NoWrap: true) { Ellipsize = true },
                TileColor, hover, click, width, En)), Width: width);
    }

    // On/Off toggle chip: same width in both states = the longest On/Off translation (+ padding, house rule 10).
    private const float OnOffChipW = 56f;

    private HudElement OnOffChip(Func<bool> get, Action<bool> set, Func<bool>? enabled = null)
        => StateChip(() => _loc.T("tl.chip.on"), () => _loc.T("tl.chip.off"), get, () => set(!get()), OnOffChipW, enabled);

    // One choice of a segmented setting (Display: Classic / List / Off …): on = the current value.
    private HudElement ChoiceChip(string labelKey, Func<bool> selected, Action pick, float width, Func<bool>? enabled = null)
    {
        Func<string> label = () => _loc.T(labelKey);
        return StateChip(label, label, selected, pick, width, enabled);
    }

    // ── round-knob slider ─────────────────────────────────────────────────────────────────────────────────────────
    // SquareHandle = a knob exactly HandleSize across (the default handle stretches to the row height = a capsule).
    // Fixed track width so every Size row lines up. `enabled` false → Unity Slider.interactable = false (the knob dims;
    // the track/fill are framework-drawn and can't be recessed from a plugin — WindowBuilder.Widgets BuildSlider).
    private const float SliderKnobSize = 14f;
    private const float SliderTrackW   = 96f;

    private static SliderElement RoundSlider(Func<float> get, Action<float> set, float min, float max, Func<bool>? enabled)
        => new(get, set, min, max, enabled) { SquareHandle = true, HandleSize = SliderKnobSize, Width = SliderTrackW };

    // ── filled list rows ──────────────────────────────────────────────────────────────────────────────────────────
    // House rule 15: the VirtualList slot STRETCHES its child to RowHeight, so a filled 26 px tile with RowHeight 28 gets
    // no gap. The pooled element is Column[tile, 2 px spacer] instead, whose preferred height IS the 28 px pitch.
    private const float ListTileHeight = 26f;
    private const float ListRowGap     = 2f;
    private const float ListRowPitch   = ListTileHeight + ListRowGap;   // the VirtualList RowHeight

    private static HudElement ListSlot(HudElement tile) => new ColumnElement(new HudElement[]
    {
        tile,
        new SpacerElement(0f, ListRowGap),
    }, Gap: 0.01f);

    // Viewport height that shows exactly `rows` rows: the last row's 2 px gap falls outside, so the list ends on a tile.
    private static float ListHeight(int rows) => rows * ListRowPitch - ListRowGap;

    // ── round icon button ─────────────────────────────────────────────────────────────────────────────────────────
    // A 28 × 28 circle (Neutral fill, lighter on hover) around a Bare selectable with a centred PLAIN glyph ("✕" can draw
    // blank in bold). Exactly its side: one Row[28 px strut, a 28 px-wide glyph box] (house rule 4). StatInspector port.
    private const float IconButtonSize = 28f;

    private HudElement IconButton(Func<string> glyph, Action onClick)
    {
        var hover = new HoverFlag();
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, IconButtonSize),
                new TextElement(glyph, Color: () => hover.On ? NeutralText() : MutedText(), Width: IconButtonSize,
                    Align: TextAlign.Center, NoWrap: true),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(hover.On ? HoverFill(NeutralFill()) : NeutralFill()),
            CornerRadius = RoundRadius(IconButtonSize), PaddingX = 0f, PaddingY = 0f,
        };
    }

    // ── cached formatter ──────────────────────────────────────────────────────────────────────────────────────────
    // A localized template + up to two numbers → the composed string, rebuilt only when the template (a language
    // switch) or a number changes. The text Funcs poll every refresh; this keeps them allocation-free in steady state.
    private sealed class FmtCache
    {
        private string? _tpl;
        private long _a = long.MinValue, _b = long.MinValue;
        private string _s = string.Empty;

        public string Get(string tpl, long a, long b = 0)
        {
            if (a != _a || b != _b || !string.Equals(tpl, _tpl, StringComparison.Ordinal))
            {
                _tpl = tpl; _a = a; _b = b;
                try { _s = string.Format(CultureInfo.CurrentCulture, tpl, a, b); }
                catch (FormatException) { _s = tpl; }   // a broken translation shows its raw text, never throws per refresh
            }
            return _s;
        }
    }
}
