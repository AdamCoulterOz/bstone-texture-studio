# Retro Texture Studio

A browser-based studio for AI-redrawing the wall and sprite art of retro games,
producing hi-res external texture packs for their source ports. Every texture is
genuinely *redrawn* — composed into sprite sheets, sent to an image model with
rich per-cell context, then sliced, alpha-keyed, hand-placed and packed — not
batch-upscaled.

Games are plugins. The first one ships in the box:

| Game | Editions | Source port |
| --- | --- | --- |
| **Blake Stone** | Aliens of Gold (full + shareware), Planet Strike | [BStone](https://github.com/bibendovsky/bstone) |

**Use it here: <https://adamcoulteroz.github.io/retro-texture-studio/>**

Everything runs client-side in your browser (Blazor WebAssembly). Your game
data, artwork and API key never touch a server of ours — the workspace is a
local folder you pick, and generation calls go straight from your browser to
the model provider with your own key.

## What you need

- A **Chromium browser** (Chrome or Edge) — the workspace uses the File
  System Access API.
- Your own legally owned **game data** for the game you pick. For Blake Stone
  that's `VSWAP.BS6` (Aliens of Gold) or `VSWAP.VSI` (Planet Strike).
- A **Gemini** or **OpenAI** API key for generation.

## Workflow

1. Open a workspace folder and choose its game (*Settings → Game*), then
   import your game data — grant a folder to search and the game's locator
   finds installed copies inside it (including inside macOS application
   bundles), or pick the file yourself.
2. Curate items and frames, arrange sheets in groups (seamless runs
   supported), attach style / character / approved-frame references.
3. Generate redraws, review & revise regions, then adjust each sprite's
   placement before applying.
4. Hit **Pack** in the title bar — the pack is written to `<workspace>/pack`,
   ready to keep anywhere on disk. The Game drawer links to the source port and
   its own instructions for loading a pack. (`TextureStudio.Pack <workspace>`
   does the same from a terminal.)

## Adding a game

Implement `IGame` plus `IGameArchive` (and optionally `IGameMetadata` for engine
reference data and `IGameLocator` to find installs) under
`src/TextureStudio.Core/Games/`, then add it to `GameCatalog`'s built-in list.
Everything downstream — sheet layout, prompts, slicing, keying, placement
tuning and packing — is already game-agnostic. See
[ARCHITECTURE.md](ARCHITECTURE.md#adding-a-game).

## Running locally

```bash
dotnet run --project src/TextureStudio.App
```

Requires a .NET SDK with the `wasm-tools` workload. The pack/re-slice CLIs
live in `src/TextureStudio.Pack` and `src/TextureStudio.Rekey`.

## Docs

- [CONTEXT.md](CONTEXT.md) — project context, conventions, open questions
- [ARCHITECTURE.md](ARCHITECTURE.md) — how the pieces fit and data flows
- [HISTORY.md](HISTORY.md) — significant decisions and turning points

## Credits & license

GPL-2.0-or-later — see [LICENSE](LICENSE). The Blake Stone plugin's VSWAP,
palette and sprite handling is ported from
[BStone](https://github.com/bibendovsky/bstone) by Boris I. Bendovsky, based on
the Blake Stone source by JAM Productions, published under the GPL by Apogee
Software. Blake Stone remains a trademark of its owners; this tool ships no
game data.
