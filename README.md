# StellarTargetLensPlugin

A [Stellar Framework](https://github.com/) plugin for **Blue Protocol: Star Resonance** that turns your current
combat target into a full, customizable readout — HP, shield, break gauge, buffs/debuffs, threat/aggro, boss skill
timers, and a live cast bar — all as draggable overlays.

## Features

- **Target HUD** — live HP, shield ("armor"), and the boss **break (stagger) gauge**, plus name, level, rank, and distance to target.
- **Buff & debuff tracker** — every effect on your target with real icons + timers. Display as **Classic tiles**, a resizable **List** window, or turn it **Off**.
  - Filter by **who applied it** — you / other players / the monster's own & unknown-source effects.
  - **★ marks your own** effects; **hide permanent** (no-timer) effects; **show hidden** internal effects.
  - **Select effects…** picker to choose exactly which buffs/debuffs appear, with a curated **★ Recommended** list floated to the top.
- **Threat / Aggro** — per-player **threat %** on the current target, with your own row highlighted.
- **Boss Skill Timers** — live **countdowns** to a boss's deadly/telegraphed skills (the game's DBM data).
- **Cast bar** — the target's channel/cast progress, counting up, with danger casts accented.
- **Fully customizable** — every overlay drags & resizes and remembers its place; settings grouped by HUD; localized in **EN / JA / TH / ID / FIL**.

## Requirements

- [Stellar Framework](https://github.com/) installed in the game directory.

## Installation

Copy `Stellar.TargetLens.dll` into:

```
<GameDir>\stellar\plugins\targetlens\
```

The plugin loads automatically when the game starts.

## Usage

1. Open the Stellar launcher overlay and click **Target Lens** to open its settings.
2. Target something in combat — the overlays auto-show (target HUD, buffs/debuffs, threat, boss timers, cast bar).
3. Use **Select effects…** to choose which buffs/debuffs appear; recommended ones are marked with a **★** and sorted to the top.
4. Drag / resize any overlay to taste; use the launcher's **"reset all HUD"** to restore defaults.

## Configuration

- Plugin settings persist in `stellar.targetlens.config.json` (in the game's `stellar\plugins\` folder).
- Window positions/sizes persist in the framework's layout store (per resolution).

## Building

- Target framework: `net6.0`. References the Stellar framework NuGet packages (`Stellar.Abstractions` / `Stellar.PluginContracts`).
- `Local.props` (gitignored) sets `GameInstallDir`; copy `Local.props.example` and point it at your install. The post-build step deploys the DLL to `<GameInstallDir>\stellar\plugins\targetlens\`.

## License

Copyright (C) 2026 speedxpz

Licensed under the [GNU Affero General Public License v3.0](LICENSE.md).
