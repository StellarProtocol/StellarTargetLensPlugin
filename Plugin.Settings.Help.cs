using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Per-setting help, house style (Maestro Plugin.Help*.cs → StatInspector / MinimalNameplate). The window framework has
/// no hover tooltip, so every setting row ends in a round 24 px "?" (Neutral, lighter on hover, accent-tinted while its
/// tip is open) that opens ONE shared, title-bar-less GlassMenu popover anchored under it:
///   [32 px accent tile with a white bold "?"] [bold setting name / muted "SECTION · HELP"] … [round ✕]
///   then the help text in one Tile box, 360 wide.
/// Each "?" passes its loc keys (title = the row label, body, section caption); the popover's text Funcs read whichever
/// set is current, so clicking another "?" while open swaps the text and re-anchors it. Re-clicking the open "?" closes
/// it. All keys are constant strings → compared by reference, no allocation per refresh.
/// </summary>
public sealed partial class Plugin
{
    private const float HelpSize     = 24f;    // the round "?" — exactly 24 × 24 (RoundRadius)
    private const float HelpTipWidth = 360f;
    private const float HelpIconSize = 32f;

    private IWindowControl _helpWindow = null!;   // registered first in RegisterSettings(); drained via _windows
    private WindowRect _helpRect;                 // where the popover should sit (re-applied for a few ticks after opening)
    private int _helpRepositionTicks;
    private readonly HoverFlag _helpCloseHover = new();
    // The open tip's loc keys ("" body = closed).
    private string _helpTitleKey = string.Empty, _helpBodyKey = string.Empty, _helpSectionKey = string.Empty;

    // Content = [24 px strut, the "?" in a 24 px box] side by side in ONE Row, so the circle is exactly its side (a
    // stacked spacer drew Maestro's "?" 24 × 26). CenterGlyphs centres the drawn "?" in its box. Always clickable, even
    // on a recessed row: the help is still useful while a setting doesn't apply.
    private HudElement HelpDot(string titleKey, string bodyKey, string sectionKey)
    {
        var hover = new HoverFlag();
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, HelpSize),
                new TextElement(() => "?", Color: () => NeutralText(), Width: HelpSize, Align: TextAlign.Center,
                    NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { })                       // the real work is in OnClickWithRect (needs the button's rect)
            {
                Bare = true,
                OnHover = over => hover.On = over,
                OnClickWithRect = r => ToggleHelp(r, titleKey, bodyKey, sectionKey),
            })
        {
            // Accent-tinted only while THIS dot's tip is the one open.
            FillColorFn = () => Fade(_helpWindow.IsShown && ReferenceEquals(_helpBodyKey, bodyKey)
                                     ? Over(AccentColor() with { A = 0.35f }, NeutralFill())
                                     : hover.On ? HoverFill(NeutralFill())
                                     : NeutralFill()),
            CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
        };
    }

    private IWindowControl RegisterHelpWindow()
    {
        IWindowControl w = null!;
        w = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.help",
                Title:       _loc.T("tl.help.title"),
                DefaultRect: new WindowRect(930f, 260f, HelpTipWidth, 0f),   // constant; every open re-anchors it
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            {
                TitleProvider = () => _loc.T("tl.help.title"),
                Draggable = true, Closable = true, StartVisible = false,
                // No title bar: the header carries the title and a close; the whole frame is the drag handle.
                ShowTitleBar = false,
                // Help for the in-world Settings window — only while in the world and not on a loading screen.
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            },
            Root: BuildHelpRoot(),
            OnClose: CloseHelp));
        return w;
    }

    private HudElement BuildHelpRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            HelpIcon(),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(HelpTitle, Color: () => TextColor(), NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                new TextElement(HelpSubtitle, Color: () => MutedText(), NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            HelpCloseButton(),
        }, Gap: 10f),
        // The help text: one raised tile (house Tile colour, theme-live), wrapping inside the 360 px popover.
        new BubbleElement(new TextElement(HelpBody, Color: () => TextColor()))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        },
    }, Gap: 10f);

    private string HelpTitle() => _helpTitleKey.Length > 0 ? _loc.T(_helpTitleKey) : string.Empty;
    private string HelpBody()  => _helpBodyKey.Length > 0 ? _loc.T(_helpBodyKey) : string.Empty;

    // "BUFFS / DEBUFFS · HELP" — composed only when the section or the language changes (string.Equals on the two short
    // localized parts), not per refresh.
    private string _helpSubSection = string.Empty, _helpSubHelp = string.Empty, _helpSub = string.Empty;

    private string HelpSubtitle()
    {
        string section = _helpSectionKey.Length > 0 ? _loc.T(_helpSectionKey) : string.Empty, help = _loc.T("tl.help.title");
        if (!string.Equals(section, _helpSubSection, StringComparison.Ordinal) || !string.Equals(help, _helpSubHelp, StringComparison.Ordinal))
        {
            _helpSubSection = section; _helpSubHelp = help;
            _helpSub = (section.Length > 0 ? section + " · " + help : help).ToUpperInvariant();
        }
        return _helpSub;
    }

    // 32 px accent tile with a white bold "?" (the icon-tile look, drawn with text instead of a PNG).
    private HudElement HelpIcon() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, HelpIconSize),
            new TextElement(() => "?", Color: () => EnsureContrast(White, AccentColor(), SolidMinContrast),
                Width: HelpIconSize, Align: TextAlign.Center, NoWrap: true, FontSize: 18) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(HelpIconSize * 0.22f), PaddingX = 0f, PaddingY = 0f,
    }, Width: HelpIconSize);

    // Round 24 px ✕ that closes the tip (plain text — a bold ✕ can draw blank): muted glyph at rest, a soft round wash +
    // text colour on hover. Rest fill is an explicit transparent (a null FillColorFn = the accent default blob).
    private HudElement HelpCloseButton() => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, HelpSize),
            new TextElement(() => "✕", Color: () => _helpCloseHover.On ? TextColor() : MutedText(), Width: HelpSize,
                Align: TextAlign.Center, NoWrap: true),
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: CloseHelp) { Bare = true, OnHover = over => _helpCloseHover.On = over })
    {
        FillColorFn = () => _helpCloseHover.On ? Fade(Over(TextColor() with { A = HoverMix }, PanelColor())) : Transparent,
        CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
    };

    // Toggle the tip: re-clicking the same "?" closes it; otherwise (closed, or another "?") show this tip's text
    // anchored under the "?" that was clicked.
    private void ToggleHelp(WindowRect r, string titleKey, string bodyKey, string sectionKey)
    {
        if (_helpWindow.IsShown && ReferenceEquals(_helpBodyKey, bodyKey)) { CloseHelp(); return; }
        _helpTitleKey = titleKey;
        _helpBodyKey = bodyKey;
        _helpSectionKey = sectionKey;
        // OnClickWithRect reports SCREEN PIXELS; SetRect takes CANVAS units — scale first, or the tip drifts off its
        // "?" whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
        var fw = _services.Framework;
        float k = fw.ScreenWidth > 0 && fw.CanvasWidth > 1f ? (float)fw.CanvasWidth / fw.ScreenWidth : 1f;
        r = new WindowRect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
        float canvasH = fw.CanvasHeight > 1f ? fw.CanvasHeight : fw.ScreenHeight;
        // Below the "?", right-aligned to it, so the popover opens to the LEFT over the row instead of off the window.
        float x = MathF.Max(10f, r.X + r.Width - HelpTipWidth);
        float y = MathF.Min(r.Y + r.Height + 4f, canvasH - 40f);
        _helpRect = new WindowRect(x, y, HelpTipWidth, 0f);
        _helpWindow.SetVisible(true);
        _helpWindow.SetRect(_helpRect);
        // Always front (house rule): the Settings window it belongs to is fronted on every open, and when switching
        // between "?"s the tip is already visible (no remount → no fresh stacking position) — CLAUDE.md Rule 5 exception.
        _helpWindow.BringToFront();
        _helpWindow.MarkDirty();   // re-pull title / body for the new keys this frame
        // The first-ever open mounts the window and applies its DefaultRect AFTER this SetRect → re-apply the anchored
        // rect for the next few ticks so it lands under the "?" on the first open too.
        _helpRepositionTicks = 4;
    }

    private void CloseHelp()
    {
        _helpRepositionTicks = 0;
        _helpBodyKey = string.Empty;
        if (_helpWindow.IsShown) _helpWindow.SetVisible(false);
    }

    // OnTargetHudUpdate: re-assert the tip position while the reposition window is open (early-out otherwise).
    private void HelpRepositionTick()
    {
        if (_helpRepositionTicks <= 0) return;
        _helpRepositionTicks--;
        if (_helpWindow.IsShown) _helpWindow.SetRect(_helpRect);
    }
}
