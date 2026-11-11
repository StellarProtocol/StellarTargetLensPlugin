using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// The effect picker ("Target Effects"), house style (Stellar Design Language; user-approved prototype
/// <c>_scratch\prototypes\targetlens-select-effects.html</c>, 2026-11-12). A fixed-size 420 px GlassMenu window:
///   ToolHeader: [40 px accent tile, white star] [bold "Target Effects" / muted "N debuffs · M buffs ticked"]
///   SHOW panel: [Only the ticked ones] [All except ticked] … [?]  — the active tab's TrackMode
///   list panel: [Debuffs n] [Buffs n] … "filtered / total" · search + ✕ · the virtual list (or "No … match") · footer
/// Rows (26 px tiles, whole row toggles the group): Plugin.SelectWindow.Rows.cs. Row flash, footer, "Untick all" +
/// its inline confirm: Plugin.SelectWindow.Feedback.cs. Table load + grouping + filter: Plugin.SelectWindow.Tables.cs.
/// Opening (centred on Settings, fronted): Plugin.Settings.Open.cs. Help popover ("?"): Plugin.Settings.Help.cs.
/// Tables load lazily the first time the window is SHOWN (game tables may not be ready at plugin init). A toggle writes
/// the config at once; the target HUD reflects it on its next rebuild (via TargetBuffTracker.Selection).
/// </summary>
public sealed partial class Plugin
{
    // Window: 420 wide, FIXED height (WindowSpec.FixedHeight), so nothing it shows — search results, the empty note,
    // the Untick-all confirm, feedback — can grow or shrink it (house rule 14). Height = title bar 32 + body padding
    // 2×12 + header ≈42 + SHOW panel ≈64 + list panel (padding 16 + tab chips 24 + search 28 + 3 gaps of 6 ≈ 86, plus
    // the list and the footer) + 2 gaps of 10 = 268 + list + footer. Normal footer 28 → the list gets ≈256 (9 rows);
    // the confirm block (≈90) takes 62 px of that → ≈194 (7 rows). The list viewport is FLEXIBLE (no
    // VirtualListElement.FixedHeight): its base height is 6 rows and it takes the column's spare height, so the
    // footer ↔ confirm swap moves the list's bottom edge, never the window's — and the content never runs taller than
    // the window (which would squeeze text too, WindowBuilder-Patterns "Fixed-height windows").
    private const float SelectWindowWidth  = 420f;
    private const float SelectWindowHeight = 552f;
    private static readonly float SelectListBaseH = ListHeight(6);   // 166
    private const float SelectGap          = 10f;   // between header / panels (the prototype's 10)
    private const float SelectPanelGap     = 6f;    // inside a panel
    // Body 396 (420 − 2×12) − panel padding 2×10 = 376 inside a panel; the search field takes it minus the ✕ and a 6 px
    // gap. Fixed: InputElement.Width wins over the row layout.
    private const float SelectInnerW       = SelectWindowWidth - 24f - 20f;
    private const float SelectSearchW      = SelectInnerW - IconButtonSize - SelectPanelGap;
    // Mode chips: both the same width = the longest label in any locale (bold 13: id "Selain yang dicentang" ≈ 140,
    // fil "Lahat maliban sa napili" ≈ 145) + padding; 2×168 + "?" 24 + 3 gaps of 4 = 372 ≤ 376 (house rule 10).
    private const float ModeChipW          = 168f;
    // Tab chips "Debuffs 12": a right-aligned label box (longest: en / fil "Debuffs" ≈ 47 bold) + a left-aligned count
    // box (3 digits), so the seam stays put while the count changes and the chip never resizes.
    private const float TabChipLabelW      = 52f;
    private const float TabChipCountW      = 28f;
    private const float TabChipW           = TabChipLabelW + 5f + TabChipCountW + 16f;   // 101

    // Loaded/assigned in the ctor (see Plugin.cs): the dedicated "select" config section + the persisted selection.
    private IConfigSection        _selCfg    = null!;
    private TargetEffectSelection _selection = null!;
    private IWindowControl        _selectWindow = null!;   // registered in RegisterSelectWindow(); drained via _windows

    private int _activeTab = 0;   // 0 = Debuffs, 1 = Buffs

    private void RegisterSelectWindow()
    {
        _selectWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.select",
                Title:       _loc.T("tl.window.select"),
                DefaultRect: new WindowRect(900f, 120f, SelectWindowWidth, SelectWindowHeight),   // constant; opens centred
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            {
                TitleProvider = () => _loc.T("tl.window.select"),   // live-localized; Title stays the fallback
                StartVisible = false, Closable = true, Draggable = true,
                FixedHeight  = true,   // the height is DefaultRect.Height, never the content's (see the sizing note above)
                // Gameplay tool: draw only while in-world, and hide during loading screens.
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
                // Framework pre-build (fw 2.30.0): the 14-row tile pool is built at the title screen and filled
                // off-screen once in the world, so the first open is a re-show. The value lambdas only read plugin
                // fields (+ _loc); the table load itself waits for a real show (SelListCount), so the fill never runs
                // the BuffTable reflection walk behind the player's back.
                Prebuild = true,
            },
            Root:    BuildSelectRoot(),
            OnClose: CloseSelectEffects));
        _windows.Add(_selectWindow);   // Dispose already loops _windows and Remove()s each
    }

    // Built ONCE; every Func re-pulls live state on the framework's capped refresh.
    private HudElement BuildSelectRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader("star-24.png", () => _loc.T("tl.window.select"), SelectStatus),
        BuildSelectShowSection(),
        BuildSelectListSection(),
    }, Gap: SelectGap);

    // SHOW: the two TrackMode choices for the ACTIVE tab + the round "?" (shared settings help popover).
    private HudElement BuildSelectShowSection() => SectionPanel(() => _loc.T("tl.sub.show"),
        () => _loc.T("tl.sel.showHint"), null, new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                ChoiceChip("tl.sel.mode.only", () => ActiveTabMode() == TrackMode.IncludeOnly,
                    () => SetActiveTabMode(TrackMode.IncludeOnly), ModeChipW),
                ChoiceChip("tl.sel.mode.except", () => ActiveTabMode() == TrackMode.ExcludeBelow,
                    () => SetActiveTabMode(TrackMode.ExcludeBelow), ModeChipW),
                new SpacerElement(0f),   // flexible: the "?" lands on the panel's right edge
                HelpDot("tl.sub.show", "tl.sel.help.mode", "tl.window.select"),
            }, Gap: 4f),
        });

    // The list panel: no text caption — its caption row IS the tab switch (two chips with live ticked counts) + the
    // muted "filtered / total" hint. Same panel look as SectionPanel.
    private HudElement BuildSelectListSection()
    {
        var caption = new RowElement(new HudElement[]
        {
            TabChip(0),
            TabChip(1),
            new SpacerElement(0f),
            new TextElement(SelectListHint, Color: () => MutedText(), NoWrap: true),
        }, Gap: 4f);

        var search = new RowElement(new HudElement[]
        {
            // One field for both tabs: Get returns the ACTIVE tab's filter, so a tab switch re-seeds it with that tab's
            // own search; typing filters the active tab only.
            new InputElement(() => _activeTab == 0 ? _dtFilter : _btFilter, SetSelectFilter, SelectSearchW,
                OnChange: SetSelectFilter)
            {
                SingleLine  = true,   // one fixed 28 px line: a long paste scrolls sideways instead of growing the panel
                Placeholder = () => _loc.T(_activeTab == 0 ? "tl.sel.search.debuffs" : "tl.sel.search.buffs"),
            },
            IconButton(() => "✕", () => SetSelectFilter(string.Empty)),
        }, Gap: SelectPanelGap);

        var pool = new HudElement[SettingsPoolSize];
        for (int i = 0; i < SettingsPoolSize; i++) pool[i] = ListSlot(BuildEffectTile(i));   // Plugin.SelectWindow.Rows.cs
        var list = new VirtualListElement(SelListCount, ListRowPitch, pool, f => _selOffset = f, Height: SelectListBaseH)
        {
            ResetScroll = ConsumeSelScrollReset,
        };

        // Search with no matches: a centred muted note in the same (flexible) list area.
        var empty = new ColumnElement(new HudElement[]
        {
            new SpacerElement(0f, SelectListBaseH / 2f + 20f),
            new TextElement(SelectEmptyText, Color: () => MutedText(), Align: TextAlign.Center, NoWrap: true)
                { Ellipsize = true },
        }, Gap: 0.01f);

        return new BubbleElement(new ColumnElement(new HudElement[]
        {
            caption,
            search,
            new ConditionalElement(SelectShowsEmpty, empty, list, Fill: true),
            BuildSelectFooter(),   // Plugin.SelectWindow.Feedback.cs
        }, Gap: SelectPanelGap))
        {
            FillColorFn = () => Fade(PanelColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        };
    }

    // One tab chip "Debuffs 12": on = bold accent label on the 22 % accent wash, off = muted on the Tile; the ticked count
    // is regular weight in both (the prototype's lighter count). Two complete chips in the same fixed cell, swapped by a
    // Conditional (bold can't toggle live), sharing one hover flag — switching tabs never moves anything.
    private HudElement TabChip(int tab)
    {
        var hover = new HoverFlag();
        string key = tab == 0 ? "tl.select.tab.debuffs" : "tl.select.tab.buffs";
        string Label() => _loc.T(key);
        string Count() => TabTickedText(tab);
        void Pick() => SwitchSelectTab(tab);
        HudElement Inner(bool on) => new RowElement(new HudElement[]
        {
            new CellElement(on
                ? DropBold(new TextElement(Label, Color: () => ChipOnText(), Align: TextAlign.Right, NoWrap: true,
                    FontSize: 13) { Bold = true, Ellipsize = true })
                : new TextElement(Label, Color: () => MutedText(), Align: TextAlign.Right, NoWrap: true) { Ellipsize = true },
                Width: TabChipLabelW),
            new CellElement(new TextElement(Count, Color: () => on ? TabCountOnColor() : MutedText(), NoWrap: true),
                Width: TabChipCountW),
        }, Gap: 5f, Justify: RowJustify.Center);
        return new CellElement(new ConditionalElement(() => _activeTab == tab,
            ChipShell(Inner(true), ChipOnFill, hover, Pick, TabChipW, () => true),
            ChipShell(Inner(false), TileColor, hover, Pick, TabChipW, () => true)), Width: TabChipW);
    }

    // The on-chip count: the accent label colour 20 % of the way to the chip wash (the prototype's opacity .8).
    private ColorRgba TabCountOnColor() => Mix(ChipOnText(), ChipOnFill(), 0.2f);

    private TrackMode ActiveTabMode() => _activeTab == 0 ? _selection.DebuffMode : _selection.BuffMode;

    private void SetActiveTabMode(TrackMode m)
    {
        if (ActiveTabMode() == m) return;
        if (_activeTab == 0) _selection.SetDebuffMode(m);
        else                 _selection.SetBuffMode(m);
        _selection.Save(_selCfg);
        CancelUntickAll();   // the confirm's consequence line depends on the mode
    }

    private void SwitchSelectTab(int tab)
    {
        if (_activeTab == tab) return;
        _activeTab = tab;
        CancelUntickAll();
        EnsureBuffTabLoaded();
        // Re-apply this tab's filter (keeps its search): re-sorts so ticked rows float to the top, and resets the scroll.
        if (tab == 0) ApplyDebuffTabFilter(_dtFilter);
        else          ApplyBuffTabFilter(_btFilter);
    }

    private void SetSelectFilter(string s)
    {
        s ??= string.Empty;
        if (string.Equals(s, _activeTab == 0 ? _dtFilter : _btFilter, StringComparison.Ordinal)) return;
        if (_activeTab == 0) ApplyDebuffTabFilter(s);
        else                 ApplyBuffTabFilter(s);
    }

    // The list's Count func. Loads the tables on the first refresh the window is really SHOWN (not during the hidden
    // pre-build fill); a failed load (tables not ready yet) retries on the next refresh while the window stays open.
    private int SelListCount()
    {
        if (!_buffTabLoaded && _selectWindow != null && _selectWindow.IsShown) EnsureBuffTabLoaded();
        return SelFiltCount;
    }

    private bool ConsumeSelScrollReset()
    {
        if (!_selScrollReset) return false;
        _selScrollReset = false;
        return true;
    }

    // Close (title-bar ✕): stop the open-centring burst, drop a pending confirm, and close the help tip if it is ours.
    private void CloseSelectEffects()
    {
        _selectWindow.SetVisible(false);
        _selectCentre?.Stop();
        CancelUntickAll();
        if (ReferenceEquals(_helpBodyKey, "tl.sel.help.mode")) CloseHelp();
    }

    // ── live text (each composed only when its inputs change) ─────────────────────────────────────────────────────
    private readonly FmtCache _selStatusFmt = new();
    private readonly FmtCache _selHintFmt   = new();
    private readonly FmtCache[] _tabCountFmt = { new(), new() };

    // Header status (the ONE status builder): ticked GROUPS per tab — "ticked" reads right in both modes.
    private string SelectStatus()
    {
        EnsureTickCounts();
        return _selStatusFmt.Get(_loc.T("tl.sel.status"), _dtTicked, _btTicked);
    }

    private string TabTickedText(int tab)
    {
        EnsureTickCounts();
        return _tabCountFmt[tab].Get("{0}", tab == 0 ? _dtTicked : _btTicked);
    }

    private string SelectListHint() => _selHintFmt.Get("{0} / {1}", SelFiltCount, _activeTab == 0 ? _dtCount : _btCount);

    private bool SelectShowsEmpty() => SelFiltCount == 0 && !string.IsNullOrWhiteSpace(SelFilter);

    // "No debuffs match “q”" — rebuilt only when the query, the tab or the language changes.
    private string? _emptyTpl, _emptyQ;
    private string _emptyText = string.Empty;

    private string SelectEmptyText()
    {
        var tpl = _loc.T(_activeTab == 0 ? "tl.sel.empty.debuffs" : "tl.sel.empty.buffs");
        var q = SelFilter;
        if (!string.Equals(tpl, _emptyTpl, StringComparison.Ordinal) || !string.Equals(q, _emptyQ, StringComparison.Ordinal))
        {
            _emptyTpl = tpl; _emptyQ = q;
            try { _emptyText = string.Format(CultureInfo.CurrentCulture, tpl, q.Trim()); }
            catch (FormatException) { _emptyText = tpl; }
        }
        return _emptyText;
    }
}
